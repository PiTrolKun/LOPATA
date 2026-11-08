using System.Globalization;
using System.IO;
using System.IO.Compression;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace AIHub.Services.OfficeEditor;

internal readonly record struct WorkbookAddress(int Row, int Column)
{
    public const int LastRow = 1_048_576, LastColumn = 16_384;
    public static string ColumnName(int column)
    {
        var value = "";
        while (column > 0)
        {
            column--;
            value = (char)('A' + column % 26) + value;
            column /= 26;
        }
        return value;
    }

    public override string ToString() => ColumnName(Column) + Row.ToString(CultureInfo.InvariantCulture);
    public static bool TryParse(string? text, out WorkbookAddress address)
    {
        address = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim().ToUpperInvariant();
        var i = 0; var column = 0;
        while (i < text.Length && text[i] is >= 'A' and <= 'Z')
        {
            column = column * 26 + text[i++] - 'A' + 1;
            if (column > LastColumn) return false;
        }
        if (column == 0 || !int.TryParse(text.AsSpan(i), NumberStyles.None, CultureInfo.InvariantCulture, out var row)
            || row is < 1 or > LastRow) return false;
        address = new(row, column);
        return true;
    }
}

internal sealed record WorkbookCell(string Display, string Input, bool IsFormula = false);
internal sealed record WorkbookSheet(string Name, string? Relation, bool IsGrid);

// Each export starts from the source package. Only the edited cells, workbook
// properties and newly created worksheets are written by the Open XML SDK.
internal sealed partial class OfficeWorkbook : IDisposable
{
    private readonly byte[] _source;
    private readonly WorkbookCalculation _calculation;
    private readonly bool _allowSavedResults;
    private readonly List<WorkbookSheet> _sheets = [];
    private readonly Dictionary<string, Dictionary<WorkbookAddress, string>> _changes = [];
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<WorkbookAddress, WorkbookCell> _cells = [];
    private readonly List<(WorkbookAddress Start, WorkbookAddress End)> _lockedRanges = [];
    private readonly List<(uint First, uint Last, double Width)> _widths = [];
    public double ColumnWidth(int column)
    {
        foreach (var span in _widths)
            if (column >= span.First && column <= span.Last) return span.Width;
        return 108;
    }
    private WorkbookSheet? _loaded;
    public IReadOnlyList<WorkbookSheet> Sheets => _sheets;
    public bool Locked { get; private set; }
    public bool SheetLocked { get; private set; }
    public bool HasUncalculatedFormulas => _calculation.HasUnavailable;
    public bool HasStoredFormulaResults => Revision == 0 && _allowSavedResults && _calculation.HasUnavailableWithCache;
    public int Revision { get; private set; }
    public int LastUsedRow { get; private set; }
    public int LastUsedColumn { get; private set; }

    internal OfficeWorkbook(byte[] bytes, string firstName)
    {
        _source = bytes.Length == 0 ? OfficeTextWork.EmptyWorkbook(firstName) : bytes;
        using var stream = new MemoryStream(_source, false);
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read, true))
        {
            if (zip.Entries.Count > 16000 || zip.Entries.Sum(e => e.Length) > OfficeTextPackage.MaxBytes)
                throw new InvalidDataException("OfficeEditor.InvalidPackage");
            Locked = zip.Entries.Any(e => e.FullName.StartsWith("_xmlsignatures/", StringComparison.OrdinalIgnoreCase));
        }
        stream.Position = 0;
        using var file = SpreadsheetDocument.Open(stream, false);
        var book = file.WorkbookPart ?? throw new InvalidDataException("OfficeEditor.InvalidPackage");
        var workbook = book.Workbook ?? throw new InvalidDataException("OfficeEditor.InvalidPackage");
        _allowSavedResults = workbook.CalculationProperties?.ForceFullCalculation?.Value != true;
        Locked |= workbook.WorkbookProtection != null;
        foreach (var sheet in workbook.Sheets?.Elements<Sheet>() ?? [])
        {
            if (sheet.Name?.Value is not { } name || sheet.Id?.Value is not { } id) continue;
            _names.Add(name);
            if (sheet.State?.Value == SheetStateValues.Hidden || sheet.State?.Value == SheetStateValues.VeryHidden) continue;
            _sheets.Add(new(name, id, book.GetPartById(id) is WorksheetPart));
        }
        if (_sheets.Count == 0) throw new InvalidDataException("OfficeEditor.InvalidPackage");
        _calculation = new WorkbookCalculation(book);
    }

    public void Dispose() => _calculation.Dispose();

    internal IReadOnlyDictionary<string, IReadOnlyDictionary<WorkbookAddress, WorkbookFormulaResult>> CalculationSnapshot() => _calculation.Snapshot;
    internal IReadOnlyDictionary<WorkbookAddress,string> ExpandedSharedFormulas(string sheetName) => _calculation.Shared(sheetName);

    public WorkbookSheet AddSheet(string nameBase)
    {
        if (Locked) throw new InvalidOperationException("OfficeEditor.WorkbookProtected");
        var index = 1;
        while (_names.Contains(nameBase + index)) index++;
        var sheet = new WorkbookSheet(nameBase + index, null, true);
        _calculation.AddEmptySheet(sheet.Name);
        _names.Add(sheet.Name); _sheets.Add(sheet); Revision++;
        return sheet;
    }

    public void Select(WorkbookSheet sheet)
    {
        if (_loaded == sheet) return;
        _cells = [];
        _lockedRanges.Clear();
        _widths.Clear();
        SheetLocked = Locked || !sheet.IsGrid;
        if (sheet.Relation != null && sheet.IsGrid)
        {
            using var stream = new MemoryStream(_source, false);
            using var file = SpreadsheetDocument.Open(stream, false);
            var book = file.WorkbookPart!;
            var page = ((WorksheetPart)book.GetPartById(sheet.Relation)).Worksheet ?? throw new InvalidDataException("OfficeEditor.InvalidPackage");
            var strings = book.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>()
                .Select(item => item.InnerText).ToArray() ?? [];
            SheetLocked |= page.GetFirstChild<SheetProtection>() != null;
            foreach (var col in page.Elements<Columns>().SelectMany(group => group.Elements<Column>()))
                if (col.Min?.Value is { } first && col.Max?.Value is { } last && col.Width?.Value is { } width && double.IsFinite(width))
                    _widths.Add((first, last, Math.Clamp(7 * width + 5, 45, 720)));
            foreach (var cell in page.GetFirstChild<SheetData>()?.Descendants<Cell>() ?? [])
            {
                if (!WorkbookAddress.TryParse(cell.CellReference?.Value, out var address)) continue;
                var value = cell.CellValue?.InnerText ?? "";
                if (cell.DataType?.Value == CellValues.SharedString && int.TryParse(value, out var i) && i >= 0 && i < strings.Length) value = strings[i];
                if (cell.DataType?.Value == CellValues.InlineString) value = cell.InlineString?.InnerText ?? "";
                if (cell.DataType?.Value == CellValues.InlineString || cell.DataType?.Value == CellValues.SharedString ||
                    cell.DataType?.Value == CellValues.String) value = DecodeCellText(value);
                if (cell.DataType?.Value == CellValues.Boolean) value = value == "1" ? "TRUE" : "FALSE";
                var formula = cell.CellFormula;
                var shared = _calculation.Shared(sheet.Name).GetValueOrDefault(address);
                var expression = shared ?? formula?.Text;
                var input = formula == null ? value : string.IsNullOrEmpty(expression) ? "" : "=" + expression;
                _cells[address] = new(value.Length > 0 ? value : input, input, formula != null);
                if (formula?.FormulaType != null && formula.FormulaType.Value != CellFormulaValues.Normal && shared == null)
                {
                    var range = formula.Reference?.Value?.Split(':');
                    if (range is { Length: 2 } && WorkbookAddress.TryParse(range[0], out var start) && WorkbookAddress.TryParse(range[1], out var end))
                        _lockedRanges.Add((start, end));
                    else _lockedRanges.Add((address, address));
                }
            }
            foreach (var merge in page.Elements<MergeCells>().SelectMany(group => group.Elements<MergeCell>()))
            {
                var ends = merge.Reference?.Value?.Split(':');
                if (ends is not { Length: 2 } || !WorkbookAddress.TryParse(ends[0], out var first) || !WorkbookAddress.TryParse(ends[1], out var last)) continue;
                if (first.Row < last.Row) _lockedRanges.Add((new(first.Row + 1, first.Column), last));
                if (first.Column < last.Column) _lockedRanges.Add((new(first.Row, first.Column + 1), new(first.Row, last.Column)));
            }
        }
        var positions = _cells.Keys.AsEnumerable();
        if (_changes.TryGetValue(sheet.Name, out var edited)) positions = positions.Concat(edited.Keys);
        LastUsedRow = LastUsedColumn = 1;
        foreach (var cell in positions)
        {
            LastUsedRow = Math.Max(LastUsedRow, cell.Row);
            LastUsedColumn = Math.Max(LastUsedColumn, cell.Column);
        }
        _loaded = sheet;
    }

    public bool CanEdit(WorkbookAddress address) => !SheetLocked && !_lockedRanges.Any(range =>
        address.Row >= range.Start.Row && address.Row <= range.End.Row && address.Column >= range.Start.Column && address.Column <= range.End.Column);

    public WorkbookCell CellAt(WorkbookAddress address)
    {
        var cell = _cells.GetValueOrDefault(address) ?? new("", "");
        if (_loaded == null) return cell;
        if (_changes.TryGetValue(_loaded.Name, out var changes) && changes.TryGetValue(address, out var value))
            cell = new(value, value, value.StartsWith('='));
        if (_calculation.Snapshot.TryGetValue(_loaded.Name, out var results) && results.TryGetValue(address, out var result))
        {
            if (result.Value != null) return cell with { Display = result.Display };
            if (HasStoredFormulaResults && _calculation.HasOriginalCache(_loaded.Name, address)) return cell;
            return cell with { Display = cell.IsFormula && cell.Input.Length > 0 ? cell.Input : "…" };
        }
        return cell;
    }

    internal static bool TryWorkbookNumber(string input, out double number)
    {
        number = 0;
        return input.Length > 0 && !input.StartsWith(' ') && !(input.Length > 1 && input[0] == '0' && char.IsDigit(input[1]))
            && (double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                || double.TryParse(input, NumberStyles.Float, CultureInfo.CurrentCulture, out number)) && double.IsFinite(number);
    }

    internal static void ValidateInput(string value)
    {
        if (value.Length > 32767 || value.StartsWith('=') && value.Length is 1 or > 8193) throw new InvalidDataException("OfficeEditor.WorkbookValueInvalid");
        System.Xml.XmlConvert.VerifyXmlChars(value);
    }

    public bool SetCell(WorkbookAddress address, string value)
    {
        if (_loaded == null || !CanEdit(address) || CellAt(address).Input == value) return false;
        ValidateInput(value);
        if (!_changes.TryGetValue(_loaded.Name, out var changes)) _changes[_loaded.Name] = changes = [];
        var restore = (_cells.GetValueOrDefault(address)?.Input ?? "") == value;
        _calculation.Edit(_loaded.Name, address, value, restore);
        if (restore) changes.Remove(address);
        else changes[address] = value;
        LastUsedRow = Math.Max(LastUsedRow, address.Row);
        LastUsedColumn = Math.Max(LastUsedColumn, address.Column);
        Revision++;
        return true;
    }
}
