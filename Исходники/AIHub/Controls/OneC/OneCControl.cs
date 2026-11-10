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
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Models.OneC;
using AIHub.Services;
using AIHub.Services.OneC;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;
using Orientation = System.Windows.Controls.Orientation;

namespace AIHub.Controls.OneC;

internal sealed partial class OneCControl : UserControl
{
    private readonly Window _owner;
    private Func<string, string> _l;
    internal OneCProjectStore Store { get; }
    internal OneCProject? Project { get; private set; }
    private readonly Func<IOneCModelSession>? _modelFactory;
    private readonly IOneCCodeChecker? _checker;
    private readonly Grid _body = new();
    private readonly StackPanel _sidebar = new() { Margin = new(10) };
    private readonly TabControl _tabs = new();
    private readonly TextBlock _title = new(), _status = new(), _modelName = new(), _saveState = new();
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, Height = 3, Visibility = Visibility.Collapsed };
    private readonly Dictionary<string, OneCCodeEditor> _editors = [];
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private readonly List<Button> _busyButtons = [];
    private Choice[] _projectChoices = [];
    private ComboBox? _projects, _sources, _contexts, _tasks, _variants;
    private TextBox? _prompt, _platform;
    private OneCCodeEditor? _proposalEditor;
    private OneCProposal? _displayedProposal;
    private bool _rendering, _busy, _entered;
    private string _mode = "build";
    private CancellationTokenSource? _cancel;
    private long _draftVersion;
    private readonly object _draftGate = new();
    private readonly Dictionary<string, long> _savedVersions = [];
    private string T(string key) => _l("OneC." + key);
    private string UiLanguage => new AppSettingsStore().LoadOrCreate().LanguageCode == "en" ? "en" : "ru";

    internal OneCControl(Window owner, Func<string, string> localize, OneCProjectStore? store = null,
        Func<IOneCModelSession>? modelFactory = null, IOneCCodeChecker? checker = null)
    {
        _owner = owner; _l = localize; Store = store ?? new(); _modelFactory = modelFactory; _checker = checker;
        Resources.MergedDictionaries.Add(new() { Source = new Uri("/AIHub;component/Controls/OneC/OneCResources.xaml", UriKind.Relative) });
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush"); SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        SetResourceReference(FontSizeProperty, "UiBodyFontSize");
        AutomationProperties.SetAutomationId(this, "OneC.View");
        _saveTimer.Tick += async (_, _) => { _saveTimer.Stop(); await SaveDraftAsync(); };
        Loaded += async (_, _) => await EnterAsync();
        Unloaded += async (_, _) => { _saveTimer.Stop(); await SaveDraftAsync(); };
        PreviewKeyDown += async (_, e) => { if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; await SaveDraftAsync(true); } };
        Build();
    }
    private void Build()
    {
        var outer = new DockPanel(); Content = outer;
        var heading = new DockPanel { Margin = new(10, 6, 10, 6) }; DockPanel.SetDock(heading, Dock.Top); outer.Children.Add(heading);
        var help = Button("Help", ShowHelpAsync, "Help"); help.Content = "?"; help.ToolTip = T("Help"); DockPanel.SetDock(help, Dock.Right); heading.Children.Add(help);
        var ai = Button("Model", ChooseModelAsync, "Model", true); ai.Content = "AI"; DockPanel.SetDock(ai, Dock.Right); heading.Children.Add(ai);
        var save = Button("SaveAll", () => SaveDraftAsync(true), "SaveAll"); DockPanel.SetDock(save, Dock.Right); heading.Children.Add(save);
        var projects = Button("ProjectsToggle", () => ChangeSidebarAsync(), "ProjectsToggle"); heading.Children.Add(projects);
        _title.VerticalAlignment = VerticalAlignment.Center; _title.FontWeight = FontWeights.SemiBold; _title.Margin = new(8, 0, 8, 0); heading.Children.Add(_title);
        var bottom = new StackPanel { Margin = new(10, 4, 10, 6) }; DockPanel.SetDock(bottom, Dock.Bottom); outer.Children.Add(bottom);
        bottom.Children.Add(_progress); _status.TextWrapping = TextWrapping.Wrap; _status.Margin = new(0, 4, 0, 0); bottom.Children.Add(_status);
        _saveState.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush"); bottom.Children.Add(_saveState);
        _body.ColumnDefinitions.Add(new() { Width = new GridLength(230) }); _body.ColumnDefinitions.Add(new()); outer.Children.Add(_body);
        var left = new ScrollViewer { Content = _sidebar, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        left.SetResourceReference(BackgroundProperty, "PanelBrush"); _body.Children.Add(left);
        Grid.SetColumn(_tabs, 1); _body.Children.Add(_tabs);
        _tabs.SelectionChanged += (_, e) =>
        {
            if (_rendering || Project is null || e.Source != _tabs) return;
            Project.ActiveFileId = (_tabs.SelectedItem as TabItem)?.Tag as string ?? ""; Dirty();
        };
    }
    internal async Task EnterAsync(string? id = null)
    {
        try
        {
            if (id is not null && Project?.Id != id) { if (!await SaveDraftAsync()) return; Project = await Task.Run(() => Store.Load(id)); }
            if (Project is null)
            {
                var projects = await Task.Run(Store.List);
                _projectChoices = projects.Select(p => new Choice(p.Id, "№" + p.Number + " · " + p.Name)).ToArray();
                Project = projects.FirstOrDefault() ?? await Task.Run(() => Store.Create(T("DefaultProject")));
            }
            if (_projectChoices.Length == 0) await RefreshProjectsAsync();
            var recovered = Project.Files.Any(f => f.RecoveryCode.Length > 0);
            Project = await Task.Run(() => Store.RecoverDrafts(Project.Id, T("RecoveredDraft")));
            _entered = true; Render();
            if (recovered) _status.Text = T("ConflictDraftSaved");
            if (Store.UnavailableProjects.Count > 0) _status.Text = T("ProjectsUnavailable") + " " + Store.UnavailableProjects.Count;
        }
        catch (Exception error) { Error(error); }
    }
    private async Task RefreshProjectsAsync()
    { _projectChoices = (await Task.Run(Store.List)).Select(p => new Choice(p.Id, "№" + p.Number + " · " + p.Name)).ToArray(); }
    internal void Configure(Func<string, string> localize) { _l = localize; if (_entered) { Checkpoint(); Render(); } }
    private Button Button(string key, Func<Task> action, string id, bool requiresIdle = false)
    {
        var b = new Button { Content = T(key), Padding = new(9, 6, 9, 6), Margin = new(3), MinHeight = 32 };
        AutomationProperties.SetAutomationId(b, "OneC." + id);
        b.Click += async (_, _) => { try { if (requiresIdle && _busy) return; await action(); } catch (OperationCanceledException) { _status.Text = T("Canceled"); } catch (Exception error) { Error(error); } };
        if (requiresIdle) _busyButtons.Add(b); return b;
    }
    private TextBlock Label(string text, bool secondary = false)
    {
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new(3, 5, 3, 3) };
        label.SetResourceReference(TextBlock.ForegroundProperty, secondary ? "TextSecondaryBrush" : "TextPrimaryBrush"); return label;
    }
    private void Dirty()
    {
        if (_rendering || Project is null) return;
        _draftVersion++; _saveState.Text = T("Unsaved"); _saveTimer.Stop(); _saveTimer.Start();
    }
    private void Busy(bool value)
    {
        _busy = value; _progress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in _busyButtons) button.IsEnabled = !value;
        RefreshComparison();
    }
    private void Error(Exception error)
    {
        var key = error.Message;
        _status.Text = key.StartsWith("OneC.", StringComparison.Ordinal) || key.StartsWith("Reader.", StringComparison.Ordinal)
            || key.StartsWith("ChatSpeech.", StringComparison.Ordinal) ? _l(key) : T("ActionFailed");
        OwnedProcessRegistry.Log("onec_action_failed", "OneC", detail: error.GetType().Name + ": " + key);
    }
    private async Task ChangeSidebarAsync()
    {
        if (Project is null) return;
        Project.SidebarVisible = !Project.SidebarVisible; Dirty(); await SaveDraftAsync(); Render();
    }
}
