using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MenuItem = System.Windows.Controls.MenuItem;
using ContextMenu = System.Windows.Controls.ContextMenu;
using Control = System.Windows.Controls.Control;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Panel = System.Windows.Controls.Panel;
using CheckBox = System.Windows.Controls.CheckBox;
using ProgressBar = System.Windows.Controls.ProgressBar;
using TabControl = System.Windows.Controls.TabControl;
using ComboBox = System.Windows.Controls.ComboBox;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AIHub.Models.OneC;
using AIHub.Services.OneC;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;

namespace AIHub.Controls.OneC;

internal sealed partial class OneCControl
{
    private sealed record Choice(string Id, string Label) { public override string ToString() => Label; }
    private Grid? _comparison;
    private OneCCodeEditor? _baseEditor;
    private CheckBox? _compareToggle;
    private TextBlock? _diffLabel, _bindingLabel, _checkStatus;
    private StackPanel? _checkNotes;
    private Button? _apply;
    private static string FileLabel(OneCFile f) => f.Reference.Length == 0 ? f.Name : f.Reference;
    private void Render()
    {
        if (Project is not { } p) return;
        _rendering = true;
        try
        {
            _busyButtons.RemoveAll(b => !b.IsLoaded);
            if (_modelName.Parent is Panel modelParent) modelParent.Children.Remove(_modelName);
            foreach (var editor in _editors.Values)
                if (editor.Parent is Panel panel) panel.Children.Remove(editor);
                else if (editor.Parent is ContentControl host) host.Content = null;
            _tabs.Items.Clear(); _sidebar.Children.Clear(); _proposalEditor = _baseEditor = null; _displayedProposal = null;
            _title.Text = "№" + p.Number + " · " + p.Name;
            _body.ColumnDefinitions[0].Width = p.SidebarVisible ? new GridLength(230) : new GridLength(0);
            _sidebar.Visibility = p.SidebarVisible ? Visibility.Visible : Visibility.Collapsed;
            BuildSidebar();
            var taskTab = new TabItem { Header = T("TaskAndChange"), Tag = "", Content = BuildTask() };
            AutomationProperties.SetAutomationId(taskTab, "OneC.TaskTab"); _tabs.Items.Add(taskTab);
            foreach (var id in p.OpenFileIds.ToArray())
            {
                if (p.Files.SingleOrDefault(f => f.Id == id) is not { } file) continue;
                var header = new StackPanel { Orientation = Orientation.Horizontal };
                header.Children.Add(Label(file.Name + (Store.CanEdit(p, file) ? "" : " · " + T("ReadOnly"))));
                var close = Button("Close", async () =>
                {
                    if (!await SaveDraftAsync()) return;
                    Project!.OpenFileIds.Remove(id); if (Project.ActiveFileId == id) Project.ActiveFileId = "";
                    Dirty(); await SaveDraftAsync(); Render();
                }, "CloseFile." + id); close.Content = "×"; close.Padding = new(5, 0, 5, 0); header.Children.Add(close);
                var tab = new TabItem { Header = header, Tag = id, Content = BuildFile(file) };
                AutomationProperties.SetAutomationId(tab, "OneC.Tab." + id); _tabs.Items.Add(tab);
                if (p.ActiveFileId == id) _tabs.SelectedItem = tab;
            }
            if (_tabs.SelectedItem is null) _tabs.SelectedItem = taskTab;
            _saveState.Text = T("Saved");
            _status.Text = p.Pending is null ? T("Ready") : T("Pending");
            Busy(_busy);
        }
        finally { _rendering = false; }
    }
    private void BuildSidebar()
    {
        var p = Project!;
        _sidebar.Children.Add(Label(T("Projects")));
        _sidebar.Children.Add(Button("NewProject", NewProjectAsync, "NewProject", true));
        _projects = new ComboBox { Margin = new(3), MinHeight = 32 };
        AutomationProperties.SetAutomationId(_projects, "OneC.ProjectList");
        foreach (var project in _projectChoices) _projects.Items.Add(project.Id == p.Id ? new Choice(p.Id, "№" + p.Number + " · " + p.Name) : project);
        _projects.SelectedItem = _projects.Items.Cast<Choice>().FirstOrDefault(c => c.Id == p.Id);
        _sidebar.Children.Add(_projects);
        _sidebar.Children.Add(Button("OpenProject", OpenProjectAsync, "OpenProject", true));
        _sidebar.Children.Add(Button("Rename", RenameProjectAsync, "Rename", true));
        _sidebar.Children.Add(Label(T("OwnedFiles")));
        _sidebar.Children.Add(Button("NewFile", CreateBlankFileAsync, "NewFile", true));
        foreach (var file in p.Files.Where(f => f.Origin == "created")) Add(file);
        _sidebar.Children.Add(Label(T("Materials")));
        _sidebar.Children.Add(Button("AddSource", AddSourceAsync, "AddSource", true));
        _sidebar.Children.Add(Button("ImportFile", ImportFileAsync, "ImportFile", true));
        _sidebar.Children.Add(Button("ImportFolder", ImportFolderAsync, "ImportFolder", true));
        foreach (var file in p.Files.Where(f => f.Origin == "material")) Add(file);
        void Add(OneCFile file)
        {
            var b = Button("Open", () => OpenFileAsync(file.Id), "File." + file.Id);
            b.Content = new TextBlock { Text = FileLabel(file), TextWrapping = TextWrapping.Wrap }; b.HorizontalContentAlignment = HorizontalAlignment.Left;
            _sidebar.Children.Add(b);
        }
    }
    private FrameworkElement BuildTask()
    {
        var p = Project!; var task = p.Task; _mode = task.Mode;
        var body = new StackPanel { Margin = new(12) };
        var header = new WrapPanel(); body.Children.Add(header);
        header.Children.Add(Label(T("Task") + " №" + (p.Tasks.IndexOf(task) + 1)));
        foreach (var (mode, key) in new[] { ("build", "MakeTask"), ("learn", "Learn") })
        {
            var b = Button(key, async () => { _mode = mode; Dirty(); await SaveDraftAsync(); Render(); }, "Mode." + mode);
            if (task.Mode == mode) { b.SetResourceReference(BackgroundProperty, "AccentBrush"); b.Foreground = Brushes.White; } header.Children.Add(b);
        }
        header.Children.Add(Button("NewTask", NewTaskAsync, "NewTask", true));
        _tasks = Choices(p.Tasks.Select(t => new Choice(t.Id, (p.Tasks.IndexOf(t) + 1) + " · " + (t.Prompt.Length == 0 ? T("NewTask") : t.Prompt[..Math.Min(50, t.Prompt.Length)]))), task.Id, "TaskList");
        _tasks.SelectionChanged += async (_, _) =>
        {
            if (_rendering || _tasks.SelectedItem is not Choice choice || Project!.ActiveTaskId == choice.Id) return;
            if (!await SaveDraftAsync()) return;
            Project.ActiveFileId = ""; Project.ActiveTaskId = choice.Id; Project.ActiveProposalId = Project.Proposals.LastOrDefault(v => v.TaskId == choice.Id)?.Id ?? "";
            _prompt = _platform = null; _contexts = null; _proposalEditor = null; _displayedProposal = null;
            _mode = Project.Task.Mode; Dirty(); await SaveDraftAsync(); Render();
        };
        if (p.Tasks.Count > 1) body.Children.Add(_tasks);
        if (task.Mode == "learn") BuildLearning(body);
        var sources = new DockPanel(); body.Children.Add(Label(T("Source"))); body.Children.Add(sources);
        var add = Button("AddSource", AddSourceAsync, "TaskAddSource", true); DockPanel.SetDock(add, Dock.Right); sources.Children.Add(add);
        _sources = Choices(new[] { new Choice("", T("NoSource")) }.Concat(p.Files.Select(f => new Choice(f.Id, FileLabel(f) + " · " + T(Store.CanEdit(p, f) ? "Owned" : "ReadOnly")))), task.SourceFileId, "SourceList");
        sources.Children.Add(_sources);
        _sources.SelectionChanged += async (_, _) =>
        {
            if (_rendering || _sources.SelectedItem is not Choice choice || Project!.Task.SourceFileId == choice.Id) return;
            if (!await SaveDraftAsync()) return;
            OneCProtocol.Bind(Project.Task, Project.Files.SingleOrDefault(f => f.Id == choice.Id));
            Dirty(); await SaveDraftAsync(); Render();
        };
        body.Children.Add(Label(Range(task), true));
        if (task.Prompt.Length == 0)
        {
            var examples = new WrapPanel(); body.Children.Add(examples);
            foreach (var (key, mode) in new[] { ("ExplainTemplate", "learn"), ("FixTemplate", "build"), ("ExampleTemplate", "build") })
                examples.Children.Add(Button(key, async () => { _prompt!.Text = T(key + "Text"); _mode = mode; Dirty(); await SaveDraftAsync(); Render(); }, key));
        }
        body.Children.Add(Label(T("Prompt")));
        _prompt = new TextBox { Text = task.Prompt, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new(3) };
        AutomationProperties.SetAutomationId(_prompt, "OneC.Prompt"); _prompt.TextChanged += (_, _) => Dirty(); body.Children.Add(_prompt);
        var context = new StackPanel();
        _contexts = Choices(new[] { "unknown", "common", "form", "object", "command", "onescript" }.Select(id => new Choice(id, T("Context." + id))), task.Context, "Context");
        _contexts.SelectionChanged += (_, _) => Dirty(); context.Children.Add(_contexts);
        context.Children.Add(Label(T("PlatformVersion")));
        _platform = new TextBox { Text = task.PlatformVersion, Margin = new(3), MaxLength = 80 }; _platform.TextChanged += (_, _) => Dirty(); context.Children.Add(_platform);
        body.Children.Add(new Expander { Header = T("ContextTitle"), Content = context, Margin = new(3) });
        var actions = new WrapPanel(); body.Children.Add(actions);
        var generate = Button("PrepareProposal", GenerateAsync, "Generate", true); generate.SetResourceReference(BackgroundProperty, "AccentBrush"); generate.Foreground = Brushes.White; actions.Children.Add(generate);
        actions.Children.Add(Button("PrepareChecker", PrepareCheckerAsync, "PrepareChecker", true));
        actions.Children.Add(Button("Pause", PauseAsync, "Pause")); actions.Children.Add(Button("Resume", ResumePendingAsync, "Resume"));
        actions.Children.Add(Button("Cancel", CancelProcessingAsync, "Cancel"));
        _modelName.Text = T("Model") + ": " + (p.ModelId.Length == 0 ? T("DefaultModel") : p.ModelId); body.Children.Add(_modelName);
        BuildResult(body);
        return new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }
    private FrameworkElement BuildFile(OneCFile file)
    {
        var body = new DockPanel { Margin = new(10) };
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); body.Children.Add(top);
        top.Children.Add(Label(FileLabel(file) + " · " + T(Store.CanEdit(Project!, file) ? "Owned" : "ReadOnly") + " · " + file.Encoding));
        var tools = new WrapPanel(); top.Children.Add(tools); var editor = FileEditor(file);
        if (editor.Text != file.Code) editor.Editor.Document.Replace(0, editor.Editor.Document.TextLength, file.Code);
        tools.Children.Add(Button("UseInTask", () => UseFileAsync(file.Id, editor), "UseFile." + file.Id));
        tools.Children.Add(Button("CopyFile", () => CopyAsync(editor.SelectionLength > 0 ? editor.SelectedText : editor.Text), "CopyFile." + file.Id));
        tools.Children.Add(Button("CheckFile", () => CheckFileAsync(file.Id), "CheckFile." + file.Id, true));
        if (Store.CanEdit(Project!, file)) tools.Children.Add(Button("ShowFile", () => ShowOwnedFileAsync(file.Id), "ShowFile." + file.Id));
        var outline = Choices(Procedures(file.Code).Select(p => new Choice(p.Line.ToString(), p.Name)), "", "Outline." + file.Id);
        outline.MinWidth = 150; outline.SelectionChanged += (_, _) => { if (outline.SelectedItem is Choice c && int.TryParse(c.Id, out var line)) editor.Jump(line); }; tools.Children.Add(outline);
        var state = file.Check is null ? T("NotChecked") : file.Check.CodeHash != OneCProjectStore.Hash(file.Code) ? T("CheckStale")
            : T(file.Check.Diagnostics.Length == 0 ? "StaticClear" : "StaticNotes") + " " + file.Check.Diagnostics.Length;
        var bottom = Label(T("SavedWithProject") + " · " + state + " · " + T("PlatformNotChecked"), true); DockPanel.SetDock(bottom, Dock.Bottom); body.Children.Add(bottom);
        body.Children.Add(editor); return body;
    }
    private ComboBox Choices(IEnumerable<Choice> choices, string selected, string id)
    {
        var combo = new ComboBox { Margin = new(3), MinHeight = 32, MaxDropDownHeight = 360 };
        foreach (var choice in choices) combo.Items.Add(choice);
        combo.SelectedItem = combo.Items.Cast<Choice>().FirstOrDefault(c => c.Id == selected);
        AutomationProperties.SetAutomationId(combo, "OneC." + id); return combo;
    }
    private string Range(OneCTask task)
    {
        var file = Project!.Files.SingleOrDefault(f => f.Id == task.SourceFileId);
        if (file is null) return T("NewCode");
        var start = Math.Clamp(task.Start, 0, file.Code.Length); var end = Math.Clamp(start + task.Length, 0, file.Code.Length);
        return T(task.WholeFile ? "WholeFile" : "Selection") + " · " + T("Lines") + " " + (file.Code[..start].Count(c => c == '\n') + 1) + "–" + (file.Code[..end].Count(c => c == '\n') + 1);
    }
    private static IEnumerable<(string Name, int Line)> Procedures(string code)
    {
        var regex = new System.Text.RegularExpressions.Regex(@"^\s*(?:Процедура|Функция|Procedure|Function)\s+([\p{L}_][\p{L}\p{N}_]*)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return code.Split('\n').Select((line, index) => (Match: regex.Match(line), Line: index + 1)).Where(x => x.Match.Success).Select(x => (x.Match.Groups[1].Value, x.Line));
    }
}
