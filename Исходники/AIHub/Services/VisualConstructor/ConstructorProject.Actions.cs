using System.IO;
using Lopata.Updates;

namespace AIHub.Services.VisualConstructor;

internal enum ConstructorActionKind { Inside, Separate, Back }

internal sealed record ConstructorAction(string Handler, string FilePath, string Source, ConstructorActionKind? Kind, string? Target);

internal sealed partial class ConstructorProject
{
    internal ConstructorAction? ReadAction(ConstructorForm owner, string? handler)
    {
        if (string.IsNullOrEmpty(handler)) return null;
        var path = IsActionId(handler) ? ActionPath(owner, handler) : FormPath(owner) + ".cs";
        SafeUpdatePath.RejectLinks(path);
        var source = File.Exists(path) ? File.ReadAllText(path) : "";
        var fields = source.Split('\n', 2)[0].TrimEnd('\r').Split(' ');
        if (IsActionId(handler) && fields.Length == 4 && fields[0] == "//" && fields[1] == "LOPATA"
            && Enum.TryParse<ConstructorActionKind>(fields[2], out var kind) && Enum.IsDefined(kind))
        {
            var target = fields[3];
            if (IsTarget(kind, target) && Normalize(source) == Normalize(ActionSource(owner, handler, kind, target)))
                return new ConstructorAction(handler, path, source, kind, target);
        }
        return new ConstructorAction(handler, path, source, null, null);
    }

    internal string WriteAction(ConstructorForm owner, string? handler, ConstructorActionKind kind, string target)
    {
        if (!IsTarget(kind, target) || kind != ConstructorActionKind.Back && ReadForms().All(form => form.Id != target))
            throw new InvalidDataException("VisualConstructor.ChooseTarget");
        var previous = ReadAction(owner, handler);
        if (previous is { Kind: null }) throw new InvalidOperationException("VisualConstructor.ManualAction");
        EnsureNavigationSource();
        handler = previous?.Handler ?? "Action_" + Guid.NewGuid().ToString("N");
        var path = ActionPath(owner, handler);
        WriteSource(path, previous?.Source, ActionSource(owner, handler, kind, target));
        return handler;
    }

    internal void SaveActionCode(ConstructorAction action, string text)
    {
        if (!ConstructorDirectory.IsWithin(DirectoryPath, action.FilePath) || !File.Exists(action.FilePath))
            throw new FileNotFoundException("VisualConstructor.CodeMissing");
        WriteSource(action.FilePath, action.Source, text);
    }

    internal void ArchiveAction(ConstructorForm owner, string handler)
    {
        if (!IsActionId(handler)) return;
        var path = ActionPath(owner, handler);
        SafeUpdatePath.RejectLinks(path);
        if (File.Exists(path)) File.Move(path, path + $"-{Guid.NewGuid():N}.draft");
    }

    private IEnumerable<string> ActionSourceFiles(ConstructorForm owner)
    {
        var folder = ActionDirectory(owner);
        SafeUpdatePath.RejectLinks(folder);
        if (!Directory.Exists(folder)) yield break;
        foreach (var path in Directory.EnumerateFiles(folder, "*.cs"))
        {
            SafeUpdatePath.RejectLinks(path);
            var id = Path.GetFileNameWithoutExtension(path);
            if (IsActionId(id) || IsChainId(id) || IsStepId(id)) yield return path;
        }
    }

    private string ActionDirectory(ConstructorForm owner) => Path.Combine(Path.GetDirectoryName(FormPath(owner))!, "Actions");

    private string ActionPath(ConstructorForm owner, string handler)
    {
        if (!IsActionId(handler)) throw new InvalidDataException("VisualConstructor.CodeMissing");
        return Path.Combine(ActionDirectory(owner), handler + ".cs");
    }

    private static bool IsActionId(string handler) => handler.StartsWith("Action_", StringComparison.Ordinal)
        && Guid.TryParseExact(handler[7..], "N", out _);

    private static bool IsTarget(ConstructorActionKind kind, string target) => kind == ConstructorActionKind.Back
        ? target == "-"
        : target == MainForm.Id || IsFormId(target);

    private static string ActionSource(ConstructorForm owner, string handler, ConstructorActionKind kind, string target)
    {
        var call = kind == ConstructorActionKind.Back
            ? "FormNavigation.Back(sender);"
            : $"FormNavigation.{kind}(sender, static () => new {target}());";
        return $$"""
            // LOPATA {{kind}} {{target}}
            namespace VisualApp;

            public partial class {{owner.Id}}
            {
                private void {{handler}}(object sender, System.Windows.RoutedEventArgs e)
                {
                    {{call}}
                }
            }
            """ + "\n";
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static void WriteSource(string path, string? previous, string text, string? backup = null)
    {
        SafeUpdatePath.RejectLinks(path);
        var folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, $".{Guid.NewGuid():N}.source-draft");
        try
        {
            File.WriteAllText(temporary, text);
            if (previous is null) { File.Move(temporary, path); return; }
            if (!string.Equals(File.ReadAllText(path), previous, StringComparison.Ordinal))
            {
                var draft = path + $"-{Guid.NewGuid():N}.draft";
                File.Move(temporary, draft);
                throw new ConstructorSaveConflict(draft);
            }
            File.Replace(temporary, path, backup);
        }
        finally { File.Delete(temporary); }
    }
}
