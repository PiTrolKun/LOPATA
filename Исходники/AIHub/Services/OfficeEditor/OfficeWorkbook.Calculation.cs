using System.Globalization;
using System.Xml;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace AIHub.Services.OfficeEditor;

internal sealed record WorkbookFormulaResult(CellValues? DataType, string? Value)
{
    internal static readonly WorkbookFormulaResult Unavailable = new(null, null);
    internal string Display => DataType == CellValues.Boolean ? (Value == "1" ? "TRUE" : "FALSE") : Value ?? "";
}

// Only values enter the calculation workbook. An invalid formula elsewhere in
// a source worksheet therefore cannot abort unrelated calculations in ClosedXML.
internal sealed partial class WorkbookCalculation : IDisposable
{
    private sealed class SheetState(IXLWorksheet values)
    {
        internal IXLWorksheet Values { get; } = values;
        internal Dictionary<WorkbookAddress, XLCellValue> Originals { get; } = [];
        internal Dictionary<WorkbookAddress, Formula> OriginalFormulas { get; } = [];
        internal Dictionary<WorkbookAddress, Formula> Formulas { get; } = [];
        internal Dictionary<WorkbookAddress, string> Shared { get; } = [];
        internal HashSet<WorkbookAddress> CachedAddresses { get; } = [];
    }

    private sealed record Formula(SheetState Sheet, WorkbookAddress At, WorkbookFormulaPlan? Plan, string? LegacyError);
    private readonly XLWorkbook _values = new();
    private readonly Dictionary<string, bool> _functions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SheetState> _sheets = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, IReadOnlyDictionary<WorkbookAddress, WorkbookFormulaResult>> _snapshot =
        new Dictionary<string, IReadOnlyDictionary<WorkbookAddress, WorkbookFormulaResult>>();
    internal bool HasUnavailable { get; private set; }
    internal bool HasUnavailableWithCache { get; private set; }
    internal bool HasOriginalCache(string sheet, WorkbookAddress at) => _sheets[sheet].CachedAddresses.Contains(at);
    internal IReadOnlyDictionary<string, IReadOnlyDictionary<WorkbookAddress, WorkbookFormulaResult>> Snapshot => _snapshot;
    internal IReadOnlyDictionary<WorkbookAddress, string> Shared(string name) => _sheets[name].Shared;

    internal WorkbookCalculation(WorkbookPart book)
    {
        _values.Use1904DateSystem = book.Workbook!.WorkbookProperties?.Date1904?.Value == true;
        var strings = book.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>().Select(s => s.InnerText).ToArray() ?? [];
        foreach (var sheet in book.Workbook!.Sheets!.Elements<Sheet>())
            if (sheet.Name?.Value is { } name && sheet.Id?.Value is { } id && book.GetPartById(id) is WorksheetPart { Worksheet: { } page })
                LoadSheet(AddSheet(name), page, strings);
        Calculate();
    }

    public void Dispose() => _values.Dispose();

    private SheetState AddSheet(string name)
    {
        var state = new SheetState(_values.Worksheets.Add(name));
        _sheets.Add(name,state);
        return state;
    }

    internal void AddEmptySheet(string name)
    {
        AddSheet(name);
        Calculate();
    }

    internal void Edit(string sheetName, WorkbookAddress at, string input, bool restore)
    {
        var sheet = _sheets[sheetName];
        if (restore && sheet.OriginalFormulas.TryGetValue(at, out var original))
            sheet.Formulas[at] = original;
        else if (!restore && input.StartsWith('='))
            sheet.Formulas[at] = new(sheet, at, WorkbookFormulaPlan.Parse(input[1..], sheetName, at), null);
        else
        {
            sheet.Formulas.Remove(at);
            var value = restore ? sheet.Originals.GetValueOrDefault(at) : InputValue(input);
            sheet.Values.Cell(at.Row, at.Column).Value = value;
        }
        Calculate();
    }

    private static XLCellValue InputValue(string input)
    {
        if (input.Length == 0) return default;
        if (input is "TRUE" or "FALSE") return input == "TRUE";
        if (OfficeWorkbook.TryWorkbookNumber(input, out var number)) return number;
        return input;
    }

    private void Calculate()
    {
        var dependencies = new Dictionary<Formula, HashSet<Formula>>();
        var dependents = new Dictionary<Formula, List<Formula>>();
        foreach (var sheet in _sheets.Values)
        foreach (var formula in sheet.Formulas.Values)
        {
            var before = new HashSet<Formula>();
            if (formula.Plan?.Expression != null)
            foreach (var area in formula.Plan.Precedents)
            {
                if (!_sheets.TryGetValue(area.Sheet, out var source)) continue;
                if (area.First == area.Last)
                {
                    if (source.Formulas.TryGetValue(area.First, out var precedent)) before.Add(precedent);
                }
                else
                    foreach (var candidate in source.Formulas.Values)
                        if (area.Contains(candidate.At)) before.Add(candidate);
            }
            dependencies.Add(formula, before);
            foreach (var precedent in before)
            {
                if (!dependents.TryGetValue(precedent, out var users)) dependents[precedent] = users = [];
                users.Add(formula);
            }
        }

        // Iteration also handles very long chains without recursion. Nodes left
        // behind after the queue is exhausted depend on a circular reference.
        var counts = dependencies.ToDictionary(p => p.Key, p => p.Value.Count);
        var ready = new Queue<Formula>(counts.Where(p => p.Value == 0).Select(p => p.Key));
        var results = new Dictionary<Formula, WorkbookFormulaResult>();
        while (ready.TryDequeue(out var current))
        {
            var result = dependencies[current].Any(f => results[f].Value == null)
                ? WorkbookFormulaResult.Unavailable : Evaluate(current);
            results.Add(current, result);
            foreach (var user in dependents.GetValueOrDefault(current) ?? [])
                if (--counts[user] == 0) ready.Enqueue(user);
        }
        var snapshot = new Dictionary<string, IReadOnlyDictionary<WorkbookAddress, WorkbookFormulaResult>>(StringComparer.OrdinalIgnoreCase);
        HasUnavailable = false;
        HasUnavailableWithCache = false;
        foreach (var (name, sheet) in _sheets)
        {
            var cells = new Dictionary<WorkbookAddress, WorkbookFormulaResult>();
            foreach (var (at, formula) in sheet.Formulas)
            {
                var result = results.GetValueOrDefault(formula) ?? WorkbookFormulaResult.Unavailable;
                HasUnavailable |= result.Value == null;
                HasUnavailableWithCache |= result.Value == null && sheet.CachedAddresses.Contains(at);
                cells.Add(at, result);
            }
            snapshot.Add(name, cells);
        }
        _snapshot = snapshot;
    }

    private bool CanCall(string function, IXLWorksheet worksheet)
    {
        if (_functions.TryGetValue(function, out var supported)) return supported;
        // ClosedXML's expression evaluator uses the 1900 epoch for these
        // functions even if its workbook is configured for 1904 dates.
        if (_values.Use1904DateSystem && function is "DATE" or "DATEVALUE" or "TODAY" or "NOW" or "DAY"
            or "DAYS360" or "DATEDIF" or "EDATE" or "EOMONTH" or "ISOWEEKNUM" or "MONTH" or "YEAR"
            or "YEARFRAC" or "WEEKDAY" or "WEEKNUM" or "NETWORKDAYS" or "NETWORKDAYS.INTL"
            or "WORKDAY" or "WORKDAY.INTL" or "TEXT" or "VALUE") supported = false;
        else
        {
            // Inspect a function once in the local value-only workbook. The
            // parser checks arity only for registered functions. Checking each
            // call matters for IFERROR(unknown(...), 0), which could otherwise
            // turn an unavailable calculation into a plausible zero.
            try
            {
                var probe = worksheet.Evaluate(function + "()", "A1");
                supported = !probe.IsError || probe.GetError() != XLError.NameNotRecognized;
            }
            catch (ClosedXML.Excel.CalcEngine.ExpressionParseException) { supported = true; }
            catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException) { supported = false; }
        }
        _functions[function] = supported;
        return supported;
    }

    private WorkbookFormulaResult Evaluate(Formula formula)
    {
        var cell = formula.Sheet.Values.Cell(formula.At.Row, formula.At.Column);
        if (formula.Plan?.Malformed == true)
        {
            cell.Value = XLError.IncompatibleValue;
            return formula.LegacyError is { } error ? new(CellValues.String, error) : new(CellValues.Error, "#VALUE!");
        }
        if (formula.Plan?.Expression == null || !formula.Plan.Functions.All(f => CanCall(f, formula.Sheet.Values)))
            return WorkbookFormulaResult.Unavailable;
        try
        {
            var value = formula.Sheet.Values.Evaluate(formula.Plan.Expression, formula.At.ToString());
            cell.Value = value;
            return value.Type switch
            {
                XLDataType.Error => new(CellValues.Error, value.ToString(CultureInfo.InvariantCulture)),
                XLDataType.Text => new(CellValues.String, value.GetText()),
                XLDataType.Boolean => new(CellValues.Boolean, value.GetBoolean() ? "1" : "0"),
                XLDataType.Blank => new(null, "0"),
                _ => new(null, XmlConvert.ToString(value.GetUnifiedNumber()))
            };
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            // Unsupported references/functions are local to this formula and its dependents.
            return WorkbookFormulaResult.Unavailable;
        }
    }
}
