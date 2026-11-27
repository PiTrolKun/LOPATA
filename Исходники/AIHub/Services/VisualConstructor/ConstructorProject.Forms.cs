using System.IO;
using System.Xml.Linq;
using Lopata.Updates;

namespace AIHub.Services.VisualConstructor;

internal sealed record ConstructorForm(string Id)
{
    internal bool IsMain => Id == "MainWindow";
    internal string WindowProperty(string name) => IsMain ? name : "Window" + name;
}

internal sealed partial class ConstructorProject
{
    internal static readonly ConstructorForm MainForm = new("MainWindow");
    internal static readonly XNamespace FormsNamespace = "clr-namespace:VisualApp";
    private string FormsPath => Path.Combine(DirectoryPath, "Forms");

    internal IReadOnlyList<ConstructorForm> ReadForms()
    {
        var forms = new List<ConstructorForm> { MainForm };
        SafeUpdatePath.RejectLinks(FormsPath);
        if (!Directory.Exists(FormsPath)) return forms;
        foreach (var path in Directory.EnumerateDirectories(FormsPath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var id = Path.GetFileName(path);
            if (!IsFormId(id)) continue;
            SafeUpdatePath.RejectLinks(path);
            var form = new ConstructorForm(id);
            var root = XDocument.Parse(ReadForm(form)).Root;
            if (root?.Name != FormsNamespace + "FormView" || (string?)root.Attribute(Xaml + "Class") != "VisualApp." + id)
                throw new InvalidDataException("VisualConstructor.UnsupportedForm");
            forms.Add(form);
        }
        return forms;
    }

    internal string ReadForm(ConstructorForm form) => File.ReadAllText(FormPath(form));

    internal string FormTitle(ConstructorForm form)
    {
        var root = XDocument.Parse(ReadForm(form)).Root!;
        return ReadLiteral((string?)root.Attribute(form.WindowProperty("Title")) ?? form.Id);
    }

    internal ConstructorForm AddForm(string title)
    {
        EnsureNavigationSource();
        SafeUpdatePath.RejectLinks(FormsPath);
        var form = new ConstructorForm("Form_" + Guid.NewGuid().ToString("N"));
        using var staged = ConstructorDirectory.Create(FormsPath);
        new XElement(FormsNamespace + "FormView",
            new XAttribute(XNamespace.Xmlns + "local", FormsNamespace),
            new XAttribute("xmlns", Wpf.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "x", Xaml),
            new XAttribute(Xaml + "Class", "VisualApp." + form.Id),
            new XAttribute("WindowTitle", XamlText(title)),
            new XAttribute("WindowWidth", "640"), new XAttribute("WindowHeight", "450"),
            new XAttribute("WindowMinWidth", "320"), new XAttribute("WindowMinHeight", "240"),
            new XElement(Wpf + "Grid")).Save(Path.Combine(staged.FullPath, "View.xaml"));
        File.WriteAllText(Path.Combine(staged.FullPath, "View.xaml.cs"), $$"""
            namespace VisualApp;

            public partial class {{form.Id}} : FormView
            {
                public {{form.Id}}() => InitializeComponent();
            }
            """ + "\n");
        WriteFormWindow(staged.FullPath, form);
        staged.MoveTo(Path.Combine(FormsPath, form.Id));
        return form;
    }

    internal void SaveForm(ConstructorForm form, string previous, XDocument document)
    {
        var path = FormPath(form);
        var directory = Path.GetDirectoryName(path)!;
        var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.xaml-draft");
        try
        {
            document.Save(temporary, SaveOptions.DisableFormatting);
            if (!string.Equals(ReadForm(form), previous, StringComparison.Ordinal))
            {
                var draft = Path.Combine(directory, $"{form.Id}-{Guid.NewGuid():N}.xaml.draft");
                File.Move(temporary, draft);
                throw new ConstructorSaveConflict(draft);
            }
            File.Replace(temporary, path, null);
        }
        finally { File.Delete(temporary); }
    }

    internal IReadOnlyList<string> ReferencingActions(ConstructorForm form) => ReadForms()
        .Where(owner => owner != form)
        .SelectMany(ActionSourceFiles)
        .Where(path => File.ReadAllText(path).Contains(form.Id, StringComparison.Ordinal))
        .Select(path => Path.GetRelativePath(DirectoryPath, path)).ToArray();

    internal void ArchiveForm(ConstructorForm form, ConstructorForm? replacement = null)
    {
        if (form.IsMain || form == ReadStartupForm())
            throw new InvalidOperationException("VisualConstructor.FormInUse");
        var links = ReferencingActions(form);
        if (links.Count > 0 && (replacement is null || replacement == form || !ReadForms().Contains(replacement)))
            throw new InvalidOperationException("VisualConstructor.FormInUse");
        var edits = replacement is null ? [] : RetargetPlan(form, replacement, links);
        var source = Path.GetDirectoryName(FormPath(form))!;
        var archive = Path.Combine(DirectoryPath, ".lopata-removed-forms");
        SafeUpdatePath.RejectLinks(archive);
        Directory.CreateDirectory(archive);
        var destination = Path.Combine(archive, Guid.NewGuid().ToString("N") + "-" + form.Id);
        if (!ConstructorDirectory.IsWithin(FormsPath, source) || !ConstructorDirectory.IsWithin(archive, destination))
            throw new InvalidDataException("VisualConstructor.UnsupportedForm");
        SafeUpdatePath.RejectLinks(destination);
        var written = new List<(string Path, string Before, string After)>();
        try
        {
            foreach (var edit in edits)
            {
                WriteSource(edit.Path, edit.Before, edit.After, edit.Path + ".previous");
                written.Add(edit);
            }
            if (ReferencingActions(form).Count != 0) throw new InvalidOperationException("VisualConstructor.FormInUse");
            Directory.Move(source, destination);
        }
        catch
        {
            foreach (var (path, before, after) in written.AsEnumerable().Reverse())
                WriteSource(path, after, before);
            throw;
        }
    }

    internal bool CanRetargetForm(ConstructorForm form, IReadOnlyList<string> links)
    {
        try { _ = RetargetPlan(form, MainForm, links); return true; }
        catch (InvalidOperationException error) when (error.Message == "VisualConstructor.ManualFormLinks") { return false; }
    }

    private (string Path, string Before, string After)[] RetargetPlan(ConstructorForm form, ConstructorForm replacement, IReadOnlyList<string> links)
    {
        var remaining = links.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var edits = new List<(string, string, string)>();
        if (remaining.Count == 0) return [];
        foreach (var owner in ReadForms().Where(candidate => candidate != form))
        {
            var layout = new ConstructorLayout(ReadForm(owner));
            var bindings = layout.Elements.Cast<XElement?>().Prepend(null).SelectMany(node =>
                ConstructorSteps.Events(owner, node).Where(name => name != "Startup" && (node ?? layout.Root).Attribute(name) is not null)
                    .Select(name => new ConstructorEventSite(owner, node is null ? null : ConstructorLayout.Name(node), name)));
            foreach (var site in bindings)
            foreach (var step in ReadChain(site).Steps)
            {
                if (step.Spec is not { Kind: ConstructorStepKind.Inside or ConstructorStepKind.Separate, Arguments: [var target] } spec
                    || target != form.Id || !remaining.Remove(Path.GetRelativePath(DirectoryPath, step.FilePath))) continue;
                var updated = spec with { Arguments = [replacement.Id] };
                var source = IsStepId(step.Id) ? StepSource(site, step.Id, updated)
                    : ActionSource(owner, step.Id, (ConstructorActionKind)spec.Kind, replacement.Id);
                edits.Add((step.FilePath, step.Source, source));
            }
        }
        if (remaining.Count > 0) throw new InvalidOperationException("VisualConstructor.ManualFormLinks");
        return edits.ToArray();
    }

    internal static string ReadLiteral(string text) => text.StartsWith("{}", StringComparison.Ordinal) ? text[2..] : text;

    internal ConstructorForm ReadStartupForm()
    {
        var path = ApplicationPath();
        var uri = (string?)XDocument.Load(path).Root?.Attribute("StartupUri");
        if (uri is null) return MainForm;
        return ReadForms().FirstOrDefault(form => string.Equals(StartupUri(form), uri, StringComparison.OrdinalIgnoreCase)) ?? MainForm;
    }

    internal void SetStartupForm(ConstructorForm form)
    {
        if (!ReadForms().Contains(form)) throw new InvalidDataException("VisualConstructor.UnsupportedForm");
        var path = ApplicationPath();
        var source = File.ReadAllText(path);
        var document = XDocument.Parse(source, LoadOptions.PreserveWhitespace);
        if (document.Root?.Name != Wpf + "Application")
            throw new InvalidDataException("VisualConstructor.UnsupportedProject");
        if (!form.IsMain)
        {
            var window = Path.Combine(Path.GetDirectoryName(FormPath(form))!, "Window.xaml");
            SafeUpdatePath.RejectLinks(window);
            if (!File.Exists(window)) throw new FileNotFoundException("VisualConstructor.UnsupportedForm");
        }
        document.Root.SetAttributeValue("StartupUri", StartupUri(form));
        WriteSource(path, source, document.ToString(SaveOptions.DisableFormatting));
    }

    private static string StartupUri(ConstructorForm form) => form.IsMain ? WindowFile : $"Forms/{form.Id}/Window.xaml";

    private string ApplicationPath()
    {
        var path = Path.Combine(DirectoryPath, "App.xaml");
        SafeUpdatePath.RejectLinks(path);
        return path;
    }

    private static void WriteFormWindow(string directory, ConstructorForm form)
    {
        var name = "Window_" + form.Id;
        new XElement(Wpf + "Window", new XAttribute(XNamespace.Xmlns + "x", Xaml),
            new XAttribute(XNamespace.Xmlns + "local", FormsNamespace),
            new XAttribute(Xaml + "Class", "VisualApp." + name),
            new XElement(FormsNamespace + form.Id, new XAttribute(Xaml + "Name", "Page")))
            .Save(Path.Combine(directory, "Window.xaml"));
        File.WriteAllText(Path.Combine(directory, "Window.xaml.cs"), $$"""
            namespace VisualApp;

            public partial class {{name}} : System.Windows.Window
            {
                public {{name}}()
                {
                    InitializeComponent();
                    Page.ApplyWindowSettings(this);
                }
            }
            """ + "\n");
    }

    private static bool IsFormId(string id) => id.StartsWith("Form_", StringComparison.Ordinal)
        && Guid.TryParseExact(id[5..], "N", out _);

    private string FormPath(ConstructorForm form)
    {
        if (!form.IsMain && !IsFormId(form.Id)) throw new InvalidDataException("VisualConstructor.UnsupportedForm");
        var path = form.IsMain ? WindowPath : Path.Combine(FormsPath, form.Id, "View.xaml");
        SafeUpdatePath.RejectLinks(path);
        return path;
    }

    private void EnsureNavigationSource()
    {
        var path = Path.Combine(DirectoryPath, "FormNavigation.cs");
        SafeUpdatePath.RejectLinks(path);
        if (!File.Exists(path)) { WriteSource(path, null, ConstructorRuntimeTemplate.Navigation); return; }
        var prior = File.ReadAllText(path);
        var legacy = ConstructorRuntimeTemplate.Navigation.Replace(
            "=> sender as Window ?? Window.GetWindow", "=> Window.GetWindow", StringComparison.Ordinal);
        if (Normalize(prior) == Normalize(legacy)) WriteSource(path, prior, ConstructorRuntimeTemplate.Navigation);
    }
}
