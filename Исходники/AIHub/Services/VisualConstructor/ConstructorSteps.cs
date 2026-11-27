using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AIHub.Services.VisualConstructor;

internal enum ConstructorStepKind
{
    Inside, Separate, Back, ReadValue, SetValue, Variable, Formula, Require, Message, ChooseFile, ReadFile, Code, PlayErrorSound,
    OpenWebPage, StartProgram, IsProcessRunning, Wait
}
internal enum ConstructorArgumentKind { Form, ReadElement, WriteElement, Variable, Value, Text, Formula, Comparison }

internal sealed record ConstructorArgument(string Key, ConstructorArgumentKind Kind);
internal sealed record ConstructorStepSpec(ConstructorStepKind Kind, string[] Arguments);
internal sealed record ConstructorEventSite(ConstructorForm Form, string? Element, string Event)
{
    internal bool IsStartup => Event == "Startup";
    internal string ClassName => IsStartup ? "App" : Form.Id;
}

internal static partial class ConstructorSteps
{
    internal static readonly string[] Comparisons = ["Equal", "NotEqual", "Greater", "Less", "AtLeast", "AtMost"];

    internal static IReadOnlyList<ConstructorArgument> Arguments(ConstructorStepKind kind) => kind switch
    {
        ConstructorStepKind.Inside or ConstructorStepKind.Separate => [new("TargetForm", ConstructorArgumentKind.Form)],
        ConstructorStepKind.ReadValue => [new("InputElement", ConstructorArgumentKind.ReadElement), new("SaveVariable", ConstructorArgumentKind.Variable)],
        ConstructorStepKind.SetValue => [new("OutputElement", ConstructorArgumentKind.WriteElement), new("Value", ConstructorArgumentKind.Value)],
        ConstructorStepKind.Variable => [new("SaveVariable", ConstructorArgumentKind.Variable), new("Value", ConstructorArgumentKind.Value)],
        ConstructorStepKind.Formula => [new("Formula", ConstructorArgumentKind.Formula), new("SaveVariable", ConstructorArgumentKind.Variable)],
        ConstructorStepKind.Require => [new("LeftValue", ConstructorArgumentKind.Value), new("Comparison", ConstructorArgumentKind.Comparison),
            new("RightValue", ConstructorArgumentKind.Value), new("OnConditionFailed", ConstructorArgumentKind.Text)],
        ConstructorStepKind.Message => [new("Value", ConstructorArgumentKind.Value)],
        ConstructorStepKind.ChooseFile => [new("SaveVariable", ConstructorArgumentKind.Variable)],
        ConstructorStepKind.ReadFile => [new("FilePathValue", ConstructorArgumentKind.Value), new("SaveVariable", ConstructorArgumentKind.Variable)],
        ConstructorStepKind.OpenWebPage => [new("WebAddressValue", ConstructorArgumentKind.Value)],
        ConstructorStepKind.StartProgram => [new("ExecutablePath", ConstructorArgumentKind.Value), new("ProgramArguments", ConstructorArgumentKind.Text)],
        ConstructorStepKind.IsProcessRunning => [new("ProcessNameValue", ConstructorArgumentKind.Value), new("SaveVariable", ConstructorArgumentKind.Variable)],
        ConstructorStepKind.Wait => [new("WaitSeconds", ConstructorArgumentKind.Value)],
        _ => []
    };

    internal static string[] Events(ConstructorForm form, XElement? element) => element is null
        ? form.IsMain ? ["Startup", "Loaded", "Unloaded", "Closing"] : ["Startup", "Loaded", "Unloaded"]
        : ConstructorLayout.Kind(element) switch
        {
            ConstructorElementKind.Button or ConstructorElementKind.Menu => ["Click"],
            ConstructorElementKind.Input => ["KeyDown"],
            ConstructorElementKind.Choice => ["SelectionChanged"],
            ConstructorElementKind.Check => ["Checked", "Unchecked"],
            _ => []
        };

    internal static bool AcceptsElement(ConstructorArgumentKind argument, XElement node)
    {
        var kind = ConstructorLayout.Kind(node);
        return argument == ConstructorArgumentKind.ReadElement
            ? kind is ConstructorElementKind.Input or ConstructorElementKind.Check or ConstructorElementKind.Choice or ConstructorElementKind.Label
            : kind is ConstructorElementKind.Input or ConstructorElementKind.Check or ConstructorElementKind.Choice
                or ConstructorElementKind.Label or ConstructorElementKind.Status or ConstructorElementKind.Button;
    }

    internal static bool Available(ConstructorStepKind kind, ConstructorEventSite site) =>
        !site.IsStartup || kind is ConstructorStepKind.Variable or ConstructorStepKind.Formula or ConstructorStepKind.Require
            or ConstructorStepKind.Message or ConstructorStepKind.ChooseFile or ConstructorStepKind.ReadFile or ConstructorStepKind.Code
            or ConstructorStepKind.PlayErrorSound or ConstructorStepKind.OpenWebPage or ConstructorStepKind.StartProgram
            or ConstructorStepKind.IsProcessRunning or ConstructorStepKind.Wait;

    internal static void Validate(ConstructorStepSpec step, ConstructorEventSite site, ConstructorProject project, ConstructorLayout layout)
    {
        if (!Enum.IsDefined(step.Kind) || !Available(step.Kind, site))
            throw new InvalidDataException("VisualConstructor.InvalidStep");
        var definitions = Arguments(step.Kind);
        if (step.Arguments.Length != definitions.Count) throw new InvalidDataException("VisualConstructor.InvalidStep");
        for (var i = 0; i < definitions.Count; i++)
        {
            var input = step.Arguments[i];
            if (input is null) throw new InvalidDataException("VisualConstructor.InvalidStep");
            switch (definitions[i].Kind)
            {
                case ConstructorArgumentKind.Form when project.ReadForms().All(form => form.Id != input):
                    throw new InvalidDataException("VisualConstructor.ChooseTarget");
                case ConstructorArgumentKind.ReadElement or ConstructorArgumentKind.WriteElement
                    when !layout.Elements.Any(element => ConstructorLayout.Name(element) == input && AcceptsElement(definitions[i].Kind, element)):
                    throw new InvalidDataException("VisualConstructor.ChooseElement");
                case ConstructorArgumentKind.Variable when !VariableName().IsMatch(input):
                    throw new InvalidDataException("VisualConstructor.InvalidVariable");
                case ConstructorArgumentKind.Comparison when !Comparisons.Contains(input, StringComparer.Ordinal):
                    throw new InvalidDataException("VisualConstructor.InvalidStep");
                case ConstructorArgumentKind.Value when input.StartsWith('$') && !input.StartsWith("$$", StringComparison.Ordinal)
                    && !VariableName().IsMatch(input[1..]):
                    throw new InvalidDataException("VisualConstructor.InvalidVariable");
                case ConstructorArgumentKind.Formula:
                    try { _ = CalculateShape(input); }
                    catch (Exception ex) when (ex is FormatException or InvalidOperationException)
                    { throw new InvalidDataException("VisualConstructor.InvalidFormula", ex); }
                    break;
            }
        }
    }

    private static string CalculateShape(string formula)
    {
        var normalized = FormulaVariable().Replace(formula, "1").Replace(',', '.');
        if (normalized.Length == 0 || !Regex.IsMatch(normalized, @"^[0-9.\s()+*/%\-]+$") || !normalized.Any(char.IsDigit))
            throw new FormatException();
        // Check the complete grammar without using user values or evaluating divisions.
        var balance = 0;
        var needsValue = true;
        var previousNumber = false;
        foreach (Match token in FormulaToken().Matches(normalized))
        {
            var value = token.Value;
            if (value == "(" && needsValue) { balance++; previousNumber = false; }
            else if (value == ")" && !needsValue && balance > 0) { balance--; previousNumber = false; }
            else if (value is "+" or "-" && needsValue) { }
            else if (value is "+" or "-" or "*" or "/" or "%" && !needsValue) { needsValue = true; previousNumber = false; }
            else if (needsValue && !previousNumber && char.IsDigit(value[0])) { needsValue = false; previousNumber = true; }
            else throw new FormatException();
        }
        if (needsValue || balance != 0) throw new FormatException();
        return normalized;
    }

    [GeneratedRegex(@"^[\p{L}_][\p{L}\p{Nd}_]{0,63}$", RegexOptions.CultureInvariant)]
    internal static partial Regex VariableName();
    [GeneratedRegex(@"\$[\p{L}_][\p{L}\p{Nd}_]*", RegexOptions.CultureInvariant)]
    private static partial Regex FormulaVariable();
    [GeneratedRegex(@"[0-9]+(?:\.[0-9]+)?|[^\s]", RegexOptions.CultureInvariant)]
    private static partial Regex FormulaToken();
}
