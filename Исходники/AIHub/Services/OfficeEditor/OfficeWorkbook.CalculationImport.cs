using System.Globalization;
using ClosedXML.Excel;
using ClosedXML.Parser;
using DocumentFormat.OpenXml.Spreadsheet;

namespace AIHub.Services.OfficeEditor;

internal sealed partial class WorkbookCalculation
{
    private static void LoadSheet(SheetState sheet, Worksheet page, string[] strings)
    {
        var cells = new Dictionary<WorkbookAddress, Cell>();
        foreach (var cell in page.GetFirstChild<SheetData>()?.Descendants<Cell>() ?? [])
            if (WorkbookAddress.TryParse(cell.CellReference?.Value, out var at)) cells[at] = cell;
        ExpandShared(sheet, cells);
        var blocked = new HashSet<WorkbookAddress>();
        foreach (var (at, cell) in cells)
        {
            var f = cell.CellFormula;
            if (f != null && f.FormulaType?.Value is { } type && type != CellFormulaValues.Normal && type != CellFormulaValues.Shared)
            {
                blocked.Add(at);
                if (Bounds(f.Reference?.Value, out var first, out var last))
                    foreach (var point in cells.Keys)
                        if (Inside(point, first, last)) blocked.Add(point);
            }
        }
        foreach (var (at, cell) in cells)
        {
            var f = cell.CellFormula;
            if (f == null && !blocked.Contains(at))
            {
                var literal = Literal(cell, strings, sheet.Values.Workbook.Use1904DateSystem);
                if (!literal.IsBlank)
                {
                    sheet.Originals[at] = literal;
                    sheet.Values.Cell(at.Row, at.Column).Value = literal;
                }
                continue;
            }
            var text = f?.FormulaType?.Value == CellFormulaValues.Shared ? sheet.Shared.GetValueOrDefault(at) : f?.Text;
            var plan = !blocked.Contains(at) && !string.IsNullOrEmpty(text) ? WorkbookFormulaPlan.Parse(text, sheet.Values.Name, at) : null;
            // Some producers use a string result for their nonstandard parse error.
            // Preserve that error verbatim, never treat an arbitrary cache as a result.
            var error = cell.CellValue?.InnerText == "#ERROR!" ? "#ERROR!" : null;
            if (cell.CellValue != null || cell.InlineString != null) sheet.CachedAddresses.Add(at);
            var entry = new Formula(sheet, at, plan, error);
            sheet.Formulas[at] = sheet.OriginalFormulas[at] = entry;
        }
    }

    private static bool Inside(WorkbookAddress point, WorkbookAddress first, WorkbookAddress last) =>
        point.Row >= first.Row && point.Row <= last.Row && point.Column >= first.Column && point.Column <= last.Column;

    private static bool Bounds(string? reference, out WorkbookAddress first, out WorkbookAddress last)
    {
        first = last = default;
        var parts = reference?.Split(':');
        return parts is { Length: 1 or 2 } && WorkbookAddress.TryParse(parts[0], out first)
            && WorkbookAddress.TryParse(parts[^1], out last) && first.Row <= last.Row && first.Column <= last.Column;
    }

    private static void ExpandShared(SheetState sheet, Dictionary<WorkbookAddress, Cell> cells)
    {
        var groups = cells.Where(p => p.Value.CellFormula?.FormulaType?.Value == CellFormulaValues.Shared)
            .GroupBy(p => p.Value.CellFormula!.SharedIndex?.Value);
        foreach (var group in groups)
        {
            if (group.Key == null) continue;
            var masters = group.Where(p => !string.IsNullOrEmpty(p.Value.CellFormula!.Text)).ToArray();
            if (masters.Length != 1) continue;
            var (origin, cell) = masters[0];
            var f = cell.CellFormula!;
            if (!Bounds(f.Reference?.Value, out var first, out var last) ||
                (long)(last.Row - first.Row + 1) * (last.Column - first.Column + 1) != group.Count() ||
                group.Any(pair => !Inside(pair.Key, first, last))) continue;
            try
            {
                var relative = FormulaConverter.ToR1C1(f.Text, origin.Row, origin.Column);
                var expanded = group.ToDictionary(pair => pair.Key, pair => pair.Key == origin ? f.Text :
                    FormulaConverter.ToA1(relative, pair.Key.Row, pair.Key.Column));
                foreach (var (at, text) in expanded) sheet.Shared[at] = text;
            }
            catch (Exception error) when (error is ParsingException or ArgumentException)
            {
                // Keep unexpandable shared groups protected and retain their original XML.
            }
        }
    }

    private static XLCellValue Literal(Cell cell, string[] strings, bool dates1904)
    {
        var type = cell.DataType?.Value;
        var value = cell.CellValue?.InnerText;
        if (type == CellValues.InlineString) return OfficeWorkbook.DecodeCellText(cell.InlineString?.InnerText ?? "");
        if (type == CellValues.SharedString)
            return int.TryParse(value,out var index) && index >= 0 && index < strings.Length ? OfficeWorkbook.DecodeCellText(strings[index]) : "";
        if (value == null) return default;
        if (type == CellValues.String) return OfficeWorkbook.DecodeCellText(value);
        if (type == CellValues.Boolean) return value is "1" or "true";
        if (type == CellValues.Date && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date))
            return ((XLCellValue)date).GetUnifiedNumber() - (dates1904 ? 1462 : 0);
        if (type == CellValues.Error)
            return value switch { "#DIV/0!" => XLError.DivisionByZero, "#NAME?" => XLError.NameNotRecognized,
                "#NULL!" => XLError.NullValue, "#NUM!" => XLError.NumberInvalid, "#REF!" => XLError.CellReference,
                "#N/A" => XLError.NoValueAvailable, _ => XLError.IncompatibleValue };
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)) return number;
        return value;
    }
}
