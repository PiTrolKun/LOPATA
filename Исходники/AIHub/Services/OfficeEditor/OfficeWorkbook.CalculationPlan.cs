using System.Text;
using ClosedXML.Parser;

namespace AIHub.Services.OfficeEditor;

// Build dependencies from parsed Excel references, so sheet names, quoted text and
// absolute references are not mistaken for cell addresses. Adapt only the private
// calculation expression; the original formula is kept for editing and export.
internal sealed class WorkbookFormulaPlan : CopyVisitor
{
    internal sealed record Area(string Sheet, WorkbookAddress First, WorkbookAddress Last)
    {
        internal bool Contains(WorkbookAddress address) => address.Row >= First.Row && address.Row <= Last.Row
            && address.Column >= First.Column && address.Column <= Last.Column;
    }

    private sealed record FunctionCall(SymbolRange Span, string Name, string[] Arguments);
    private readonly List<(SymbolRange Span, Area Area, bool Qualified)> _references = [];
    private readonly List<FunctionCall> _calls = [];
    internal List<Area> Precedents { get; } = [];
    internal IEnumerable<string> Functions => _calls.Select(call => call.Name);
    internal string? Expression { get; private set; }
    internal bool Malformed { get; private set; }
    private bool _unsupported;

    internal static WorkbookFormulaPlan Parse(string input, string sheet, WorkbookAddress at)
    {
        var plan = new WorkbookFormulaPlan();
        try
        {
            FormulaConverter.ModifyA1(input, sheet, at.Row, at.Column, plan);
            plan.Finish(input, at);
        }
        catch (ParsingException) { plan.Malformed = true; }
        catch (ArgumentException) { plan.Malformed = true; }
        return plan;
    }

    private void Capture(SymbolRange span, string sheet, ReferenceArea area, bool qualified)
    {
        var a = area.First; var b = area.Second;
        int r1 = a.IsColumn ? 1 : Math.Min(a.RowValue, b.RowValue), r2 = a.IsColumn ? WorkbookAddress.LastRow : Math.Max(a.RowValue, b.RowValue);
        int c1 = a.IsRow ? 1 : Math.Min(a.ColumnValue, b.ColumnValue), c2 = a.IsRow ? WorkbookAddress.LastColumn : Math.Max(a.ColumnValue, b.ColumnValue);
        _references.Add((span, new(sheet, new(r1, c1), new(r2, c2)), qualified));
    }

    public override TransformedSymbol Reference(ModContext ctx, SymbolRange span, ReferenceArea area)
    {
        Capture(span, ctx.Sheet, area, false);
        return base.Reference(ctx, span, area);
    }

    public override TransformedSymbol SheetReference(ModContext ctx, SymbolRange span, string sheet, ReferenceArea area)
    {
        Capture(span, sheet, area, true);
        return base.SheetReference(ctx, span, sheet, area);
    }

    public override TransformedSymbol Function(ModContext ctx, SymbolRange span, ReadOnlySpan<char> name, IReadOnlyList<TransformedSymbol> args)
    {
        var fn = name.ToString().ToUpperInvariant();
        if (fn is "INDIRECT" or "OFFSET" or "WEBSERVICE" or "RTD" or "CALL" or "REGISTER.ID" or "CELL" or "FORMULATEXT" or "ISFORMULA"
            or "SUBTOTAL" or "AGGREGATE" or "SHEET" or "SHEETS" ||
            fn.StartsWith("_XLFN.") || fn.StartsWith("_XLUDF.")) _unsupported = true;
        _calls.Add(new(span, fn, args.Select(a => a.ToString()).ToArray()));
        return base.Function(ctx, span, name, args);
    }

    private void Finish(string input, WorkbookAddress at)
    {
        if (_unsupported) return;
        var adapted = new StringBuilder(input);
        foreach (var (span, area, qualified) in _references.AsEnumerable().Reverse())
        {
            var call = _calls.FirstOrDefault(f => f.Span.Start <= span.Start && f.Span.End >= span.End);
            bool directArgument = call?.Arguments.Contains(input.Substring(span.Start, span.Length), StringComparer.Ordinal) == true;
            if (directArgument && call!.Name is "ROW" or "ROWS" or "COLUMN" or "COLUMNS" or "ISREF") continue;
            if (area.First == area.Last) { Precedents.Add(area); continue; }
            if (call != null && !ScalarFunction(call.Name))
            {
                var size = (long)(area.Last.Row - area.First.Row + 1) * (area.Last.Column - area.First.Column + 1);
                // These aggregates enumerate populated cells. Do not materialize a
                // million-row operand for array functions or nested array arithmetic.
                if (size > 100_000 && (!(call.Name is "SUM" or "COUNT" or "COUNTA" or "AVERAGE" or "MIN" or "MAX" or "INDEX") ||
                    !directArgument)) return;
                Precedents.Add(area);
                continue;
            }
            if (call != null && _calls.Any(outer => outer != call && outer.Span.Start < call.Span.Start &&
                outer.Span.End >= call.Span.End && !ScalarFunction(outer.Name))) return;

            // A normal (non-array) scalar formula intersects a column with its
            // own row, or a row with its own column. Restrict dependencies too:
            // later totals in that same column must not form false cycles.
            WorkbookAddress? point = null;
            if (area.First.Column == area.Last.Column && at.Row >= area.First.Row && at.Row <= area.Last.Row)
                point = new(at.Row, area.First.Column);
            else if (area.First.Row == area.Last.Row && at.Column >= area.First.Column && at.Column <= area.Last.Column)
                point = new(area.First.Row, at.Column);
            string replacement = "#VALUE!";
            if (point is { } cell)
            {
                Precedents.Add(new(area.Sheet, cell, cell));
                replacement = (qualified ? "'" + area.Sheet.Replace("'", "''") + "'!" : "") + cell;
            }
            adapted.Remove(span.Start, span.Length).Insert(span.Start, replacement);
        }
        Expression = adapted.ToString();
    }

    private static bool ScalarFunction(string name) => name is "IF" or "IFERROR" or "IFNA" or "ABS" or "INT" or "SIGN"
        or "ROUND" or "ROUNDUP" or "ROUNDDOWN" or "MOD" or "SQRT" or "POWER" or "LEN" or "LOWER" or "UPPER";

    private TransformedSymbol Unavailable(ModContext ctx, SymbolRange span)
    {
        _unsupported = true;
        return TransformedSymbol.CopyOriginal(ctx.Formula, span);
    }

    public override TransformedSymbol BinaryNode(ModContext c, SymbolRange s, BinaryOperation operation, TransformedSymbol left, TransformedSymbol right)
        => operation is BinaryOperation.Range or BinaryOperation.Union or BinaryOperation.Intersection
            ? Unavailable(c,s) : base.BinaryNode(c,s,operation,left,right);
    public override TransformedSymbol Name(ModContext c, SymbolRange s, string name) => Unavailable(c,s);
    public override TransformedSymbol SheetName(ModContext c, SymbolRange s, string sheet, string name) => Unavailable(c,s);
    public override TransformedSymbol BangName(ModContext c, SymbolRange s, string name) => Unavailable(c,s);
    public override TransformedSymbol BangReference(ModContext c, SymbolRange s, ReferenceArea a) => Unavailable(c,s);
    public override TransformedSymbol Reference3D(ModContext c, SymbolRange s, string from, string to, ReferenceArea a) => Unavailable(c,s);
    public override TransformedSymbol StructureReference(ModContext c, SymbolRange s, StructuredReferenceArea area, string? a, string? b) => Unavailable(c,s);
    public override TransformedSymbol StructureReference(ModContext c, SymbolRange s, string table, StructuredReferenceArea area, string? a, string? b) => Unavailable(c,s);
    public override TransformedSymbol ExternalStructureReference(ModContext c, SymbolRange s, int book, string table, StructuredReferenceArea area, string? a, string? b) => Unavailable(c,s);
    public override TransformedSymbol ExternalSheetReference(ModContext c, SymbolRange s, int book, string sheet, ReferenceArea a) => Unavailable(c,s);
    public override TransformedSymbol ExternalReference3D(ModContext c, SymbolRange s, int book, string a, string b, ReferenceArea area) => Unavailable(c,s);
    public override TransformedSymbol ExternalName(ModContext c, SymbolRange s, int book, string name) => Unavailable(c,s);
    public override TransformedSymbol ExternalSheetName(ModContext c, SymbolRange s, int book, string sheet, string name) => Unavailable(c,s);
    public override TransformedSymbol ExternalFunction(ModContext c, SymbolRange s, int book, ReadOnlySpan<char> name, IReadOnlyList<TransformedSymbol> args) => Unavailable(c,s);
    public override TransformedSymbol ExternalFunction(ModContext c, SymbolRange s, int book, string sheet, ReadOnlySpan<char> name, IReadOnlyList<TransformedSymbol> args) => Unavailable(c,s);
    public override TransformedSymbol CellFunction(ModContext c, SymbolRange s, RowCol cell, IReadOnlyList<TransformedSymbol> args) => Unavailable(c,s);
    public override TransformedSymbol Function(ModContext c, SymbolRange s, string sheet, ReadOnlySpan<char> name, IReadOnlyList<TransformedSymbol> args) => Unavailable(c,s);
}
