using System.IO;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace AIHub.Services.OfficeEditor;

internal sealed partial class OfficeWorkbook
{
    private static WorksheetPart AddPart(WorkbookPart book, string name)
    {
        var sheets = book.Workbook?.Sheets ?? throw new InvalidDataException("OfficeEditor.InvalidPackage");
        var nextId = sheets.Elements<Sheet>().Select(sheet => sheet.SheetId?.Value ?? 0U).DefaultIfEmpty().Max() + 1;
        var part = book.AddNewPart<WorksheetPart>();
        part.Worksheet = new Worksheet(new SheetData());
        part.Worksheet.Save();
        sheets.Append(new Sheet { Name = name, SheetId = nextId, Id = book.GetIdOfPart(part) });
        return part;
    }

    public Task<byte[]> ExportAsync()
    {
        var sheets = _sheets.ToArray();
        var changed = _changes.Where(pair => pair.Value.Count > 0)
            .ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        if (changed.Count == 0 && sheets.All(sheet => sheet.Relation != null)) return Task.FromResult(_source);
        var results = CalculationSnapshot();
        var shared = changed.Keys.ToDictionary(name => name, ExpandedSharedFormulas);
        return Task.Run(() =>
        {
            using var stream = new MemoryStream();
            stream.Write(_source); stream.Position = 0;
            using (var file = SpreadsheetDocument.Open(stream, true, new OpenSettings { AutoSave = false }))
            {
                var book = file.WorkbookPart!;
                var workbook = book.Workbook ?? throw new InvalidDataException("OfficeEditor.InvalidPackage");
                foreach (var added in sheets.Where(sheet => sheet.Relation == null)) AddPart(book, added.Name);
                foreach (var sheet in workbook.Sheets!.Elements<Sheet>())
                {
                    if (sheet.Id?.Value is not { } id || sheet.Name?.Value is not { } name ||
                        book.GetPartById(id) is not WorksheetPart part) continue;
                    changed.TryGetValue(name, out var edits);
                    results.TryGetValue(name, out var cached);
                    var page = part.Worksheet ?? throw new InvalidDataException("OfficeEditor.InvalidPackage");
                    if (edits != null)
                    {
                        ExpandEditedSharedGroups(page, edits, shared[name]);
                        foreach (var (address, input) in edits) WriteCell(page, address, input);
                    }
                    var updated = WriteFormulaCaches(page, cached);
                    if (updated || edits != null) page.Save();
                }
                if (book.CalculationChainPart is { } chain) book.DeletePart(chain);
                workbook.CalculationProperties ??= new CalculationProperties();
                workbook.CalculationProperties.FullCalculationOnLoad = true;
                workbook.CalculationProperties.ForceFullCalculation = true;
                workbook.Save();
            }
            return stream.ToArray();
        });
    }

    private static void ExpandEditedSharedGroups(Worksheet page, KeyValuePair<WorkbookAddress, string>[] edits,
        IReadOnlyDictionary<WorkbookAddress, string> expanded)
    {
        if (expanded.Count == 0) return;
        var touched = edits.Select(pair => pair.Key).Where(expanded.ContainsKey).ToHashSet();
        if (touched.Count == 0) return;
        var groups = page.GetFirstChild<SheetData>()!.Descendants<Cell>()
            .Where(c => c.CellFormula?.FormulaType?.Value == CellFormulaValues.Shared && c.CellFormula.SharedIndex != null)
            .GroupBy(c => c.CellFormula!.SharedIndex!.Value);
        foreach (var group in groups)
        {
            if (!group.Any(c => WorkbookAddress.TryParse(c.CellReference?.Value, out var at) && touched.Contains(at))) continue;
            foreach (var cell in group)
            {
                if (!WorkbookAddress.TryParse(cell.CellReference?.Value, out var at) || !expanded.TryGetValue(at, out var text))
                    throw new InvalidDataException("OfficeEditor.WorkbookProtected");
                var formula = cell.CellFormula!;
                formula.FormulaType = null;
                formula.SharedIndex = null;
                formula.Reference = null;
                formula.Text = text;
            }
        }
    }

    private static bool WriteFormulaCaches(Worksheet worksheet, IReadOnlyDictionary<WorkbookAddress, WorkbookFormulaResult>? results)
    {
        var updated = false;
        foreach (var cell in worksheet.GetFirstChild<SheetData>()?.Descendants<Cell>() ?? [])
        {
            if (!WorkbookAddress.TryParse(cell.CellReference?.Value, out var address)) continue;
            WorkbookFormulaResult? result = null;
            if (results != null && results.TryGetValue(address, out var cached)) result = cached;
            else if (cell.CellFormula == null) continue;

            var value = result?.Value;
            var type = value == null ? null : result?.DataType;
            if (value != null && type == CellValues.String) value = EncodeCellText(value);
            var oldType = cell.DataType?.Value;
            if (cell.CellValue?.InnerText == value && (oldType ?? CellValues.Number) == (type ?? CellValues.Number) &&
                cell.InlineString == null) continue;
            cell.CellValue = value == null ? null : new CellValue(value);
            cell.DataType = type is { } dataType ? new EnumValue<CellValues>(dataType) : null;
            cell.InlineString = null;
            updated = true;
        }
        return updated;
    }

    private static void WriteCell(Worksheet page, WorkbookAddress address, string input)
    {
        var rows = page.GetFirstChild<SheetData>() ?? throw new InvalidDataException("OfficeEditor.InvalidPackage");
        var row = rows.Elements<Row>().FirstOrDefault(r => r.RowIndex?.Value >= address.Row);
        if (row?.RowIndex?.Value != address.Row)
            row = rows.InsertBefore(new Row { RowIndex = (uint)address.Row }, row);
        var cell = row.Elements<Cell>().FirstOrDefault(c =>
            WorkbookAddress.TryParse(c.CellReference?.Value, out var current) && current.Column >= address.Column);
        var reference = address.ToString();
        if (!string.Equals(cell?.CellReference?.Value, reference, StringComparison.OrdinalIgnoreCase))
            cell = row.InsertBefore(new Cell { CellReference = reference }, cell ?? (OpenXmlElement?)row.GetFirstChild<ExtensionList>());
        cell!.CellFormula = null; cell.CellValue = null; cell.InlineString = null; cell.DataType = null;
        if (input.StartsWith('='))
            cell.CellFormula = new CellFormula(input[1..]);
        else if (input is "TRUE" or "FALSE")
        {
            cell.DataType = CellValues.Boolean;
            cell.CellValue = new CellValue(input == "TRUE");
        }
        else if (TryWorkbookNumber(input, out var number))
            cell.CellValue = new CellValue(number);
        else if (input.Length > 0)
        {
            cell.DataType = CellValues.InlineString;
            cell.InlineString = new InlineString(new Text(EncodeCellText(input)) { Space = SpaceProcessingModeValues.Preserve });
        }
        // A stale A1 range can hide a newly entered cell in other consumers.
        if (page.SheetDimension is { } dimension)
        {
            var range = dimension.Reference?.Value?.Split(':');
            if (range is { Length: > 0 } && WorkbookAddress.TryParse(range[0], out var first)
                && WorkbookAddress.TryParse(range[^1], out var last))
                dimension.Reference = new WorkbookAddress(Math.Min(first.Row, address.Row), Math.Min(first.Column, address.Column))
                    + ":" + new WorkbookAddress(Math.Max(last.Row, address.Row), Math.Max(last.Column, address.Column));
        }
    }

    // SpreadsheetML ST_Xstring escapes controls (including CR) and literal
    // text which already looks like an escape. LF, TAB and valid surrogate
    // pairs are stored directly in element content.
    [GeneratedRegex(@"_(?=x[0-9A-Fa-f]{4}_)|[\x00-\x08\x0B-\x1F\uFFFE\uFFFF]|[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]")]
    private static partial Regex EscapableCellCharacters();

    [GeneratedRegex(@"_x[0-9A-Fa-f]{4}_")]
    private static partial Regex EscapedCellCharacters();

    private static string EncodeCellText(string value) =>
        EscapableCellCharacters().Replace(value, match => $"_x{(int)match.Value[0]:X4}_");

    internal static string DecodeCellText(string value) => value.Contains("_x", StringComparison.Ordinal)
        ? EscapedCellCharacters().Replace(value, match => ((char)Convert.ToUInt16(match.Value.Substring(2, 4), 16)).ToString())
        : value;
}
