using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.GameSounds;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;
using Orientation = System.Windows.Controls.Orientation;

namespace AIHub.Controls.GameSounds;

internal sealed partial class GameSoundsControl : UserControl
{
    private readonly Window _owner;
    private readonly Func<string> _language;
    private Func<string, string> _localize;
    private readonly GameSoundProjects _projects;
    private readonly GameSoundModelSession _model = new();
    private readonly GameSoundPlayer _player = new();
    private readonly GameSoundPlayer _savedPlayer = new();
    private readonly DispatcherTimer _save = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly DispatcherTimer _preview = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly StackPanel _left = new(), _results = new(), _files = new(), _history = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap }, _projectTitle = new(), _layerCount = new();
    private readonly Border _current = new() { Padding = new(8), BorderThickness = new(1), CornerRadius = new(6), Margin = new(0, 4, 0, 7) };
    private readonly ScrollViewer _chatScroll;
    private readonly TextBox _input;
    private readonly CheckBox _orchestration;
    private readonly ComboBox _mode;
    private readonly Button _openMixer, _send, _pause, _resume, _cancel;
    private Expander? _thinking;
    private TextBox? _thought;
    private readonly Dictionary<string, GameSoundRecipe> _previous = [];
    private bool _rendering, _switching, _dirty, _entered, _exporting;
    private Task? _running, _leaving;
    private CancellationTokenSource? _attempt, _warm;
    private Window? _mixer;
    private GameSoundProject Project { get; set; } = new();
    private ManagedModelArtifactCard[] _cards = [];

    internal GameSoundsControl(Window owner, Func<string, string> localize, Func<string> language, GameSoundProjects? projects = null)
    {
        _owner = owner; _localize = localize; _language = language; _projects = projects ?? GameSoundProjects.Default;
        Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/AIHub;component/Controls/SettingsResources.xaml", UriKind.Relative) });
        AutomationProperties.SetAutomationId(this, "GameSounds.Workspace");
        _current.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        _current.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        _projectTitle.VerticalAlignment = VerticalAlignment.Center;
        _projectTitle.Margin = new(8, 0, 0, 0);

        var root = new DockPanel();
        var top = Row();
        top.Children.Add(Action("Projects", ProjectsAsync));
        top.Children.Add(Action("SaveProject", () => Checkpoint(force: true)));
        top.Children.Add(_projectTitle);
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        DockPanel.SetDock(_status, Dock.Bottom); root.Children.Add(_status);
        var body = new Grid { Margin = new(0, 8, 0, 6) };
        body.ColumnDefinitions.Add(new() { Width = new(280), MinWidth = 200 });
        body.ColumnDefinitions.Add(new() { Width = new(6) });
        body.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star), MinWidth = 210 });
        body.ColumnDefinitions.Add(new() { Width = new(6) });
        body.ColumnDefinitions.Add(new() { Width = new(325), MinWidth = 170 });
        Add(body, Scroll(_left), 0);
        Add(body, Split(true), 1);

        var center = new Grid();
        center.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star), MinHeight = 110 });
        center.RowDefinitions.Add(new() { Height = new(6) });
        center.RowDefinitions.Add(new() { Height = new(120), MinHeight = 85 });
        var conversation = new DockPanel();
        var mix = Row();
        _orchestration = Check("Orchestration", false, value =>
        {
            if (_rendering) return;
            Project.Orchestrate = value; Changed(); RenderResults();
        });
        mix.Children.Add(_orchestration);
        _layerCount.Margin = new(8, 4, 9, 0); _layerCount.VerticalAlignment = VerticalAlignment.Center; mix.Children.Add(_layerCount);
        _openMixer = Action("OpenMixer", OpenMixer); mix.Children.Add(_openMixer);
        DockPanel.SetDock(mix, Dock.Top); conversation.Children.Add(mix);
        _chatScroll = Scroll(_history);
        conversation.Children.Add(_chatScroll);
        Add(center, conversation, row: 0);
        Add(center, Split(false), row: 1);

        var compose = new DockPanel();
        var composeActions = Row();
        _mode = Select([T("Discussion"), T("Generation")]);
        _mode.Margin = new(0, 3, 5, 3);
        AutomationProperties.SetName(_mode, T("Mode"));
        _mode.SelectionChanged += (_, _) =>
        {
            if (_rendering || _mode.SelectedIndex < 0) return;
            Project.Generate = _mode.SelectedIndex == 1; Changed();
        };
        composeActions.Children.Add(_mode);
        _send = Action("Send", SendAsync, primary: true); composeActions.Children.Add(_send);
        _pause = Action("Pause", PauseAsync); composeActions.Children.Add(_pause);
        _resume = Action("Resume", ContinueAsync); composeActions.Children.Add(_resume);
        _cancel = Action("Cancel", CancelAsync); composeActions.Children.Add(_cancel);
        DockPanel.SetDock(composeActions, Dock.Bottom); compose.Children.Add(composeActions);
        _input = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new(7), ToolTip = T("Prompt") };
        ThemeInput(_input);
        AutomationProperties.SetAutomationId(_input, "GameSounds.Prompt");
        AutomationProperties.SetName(_input, T("Prompt"));
        _input.TextChanged += (_, _) =>
        {
            if (_rendering) return;
            Project.Draft = _input.Text; Changed(); UpdateCommands();
        };
        compose.Children.Add(_input);
        Add(center, compose, row: 2);
        Add(body, center, 2);
        Add(body, Split(true), 3);

        var right = new Grid { Margin = new(5, 0, 0, 0) };
        right.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star), MinHeight = 65 });
        right.RowDefinitions.Add(new() { Height = GridLength.Auto });
        right.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star), MinHeight = 65 });
        Add(right, ResultSection("Recipes", _results), row: 0);
        Add(right, _current, row: 1);
        Add(right, ResultSection("SavedFiles", _files), row: 2);
        Add(body, right, 4);
        root.Children.Add(body); Content = root;

        _save.Tick += (_, _) => { _save.Stop(); if (_dirty && Project.Persistent) Checkpoint(); };
        _preview.Tick += async (_, _) =>
        {
            _preview.Stop();
            try { await _player.PrepareAsync(Project.Sources(false), CancellationToken.None); }
            catch (OperationCanceledException) { }
            catch (Exception error) { Report(error); }
        };
    }

    internal void Configure(Func<string, string> localize)
    {
        _localize = localize;
        _rendering = true;
        _mode.ItemsSource = new[] { T("Discussion"), T("Generation") };
        AutomationProperties.SetName(_mode, T("Mode"));
        _orchestration.Content = Label("Orchestration");
        _input.ToolTip = T("Prompt"); AutomationProperties.SetName(_input, T("Prompt"));
        Render();
        TranslateStaticControls(this);
    }
    internal bool IsEntered => _entered;
    private string T(string key) => _localize("GameSounds." + key);
    private string F(string key, params object[] values) => string.Format(T(key), values);
    private void Changed()
    {
        _dirty = true;
        _save.Stop(); _save.Start();
    }

    internal bool Checkpoint(bool force = false)
    {
        _save.Stop();
        if (!_dirty && !force || !Project.Persistent && !force) return true;
        try { _projects.Save(Project); _dirty = false; UpdateTitle(); return true; }
        catch (Exception error) { Report(error); return false; }
    }

    private void UpdateTitle() => _projectTitle.Text = F("ProjectCaption", Project.Number,
        string.IsNullOrWhiteSpace(Project.Name) ? T("Untitled") : Project.Name);

    private void Render(bool refreshHistory = true)
    {
        _rendering = true;
        _input.Text = Project.Draft;
        _mode.SelectedIndex = Project.Generate ? 1 : 0;
        _orchestration.IsChecked = Project.Orchestrate;
        UpdateTitle(); RenderLeft(); RenderResults(); RenderFiles();
        if (refreshHistory) RenderHistory();
        _rendering = false;
        UpdateCommands();
        InvalidatePreview();
    }

    private void UpdateCommands()
    {
        var running = _running is { IsCompleted: false };
        var pending = Project.Pending is not null;
        _send.IsEnabled = !_switching && !running && !pending && !string.IsNullOrWhiteSpace(Project.Draft);
        _cancel.IsEnabled = !_switching && pending;
        _pause.IsEnabled = !_switching && running && Project.Pending is not null &&
            PendingOperation?.Phase == BackgroundOperationPhase.Running;
        _resume.IsEnabled = !_switching && pending && (!running ||
            PendingOperation?.Phase == BackgroundOperationPhase.Paused);
    }

    private void Report(Exception error)
    {
        var message = error.Message.StartsWith("GameSounds.", StringComparison.Ordinal) ? _localize(error.Message) : error.Message;
        _status.Text = F("Error", message);
        OwnedProcessRegistry.Log("game_sounds_error", "GameSounds", detail: error.ToString());
    }

    private void RenderHistory()
    {
        _history.Children.Clear(); _thinking = null; _thought = null;
        foreach (var message in Project.Messages) AddMessage(message);
    }

    private void AddMessage(GameSoundMessage message)
    {
        var block = SelectableChatText(message.Kind switch
        {
            "user" => F("UserMessage", message.Text), "made" => F("MadeRecipe", message.Text),
            "updated" => F("UpdatedRecipe", message.Text), "log" => F("LogMessage", _localize(message.Text)),
            "answer" => message.Text, _ => message.Text
        }, secondary: message.Kind == "log");
        if (message.Kind == "thought")
        {
            _history.Children.Add(new Expander { Header = T("Thinking"), Content = block, IsExpanded = false });
            return;
        }
        _history.Children.Add(block);
    }

    private void AddLog(string key)
    {
        var log = new GameSoundMessage("log", "GameSounds." + key);
        Project.Messages.Add(log); AddMessage(log); Changed();
        ScrollChatToEnd();
    }

    private void InvalidatePreview()
    {
        _player.Invalidate(); _preview.Stop();
        if (Project.Selected is not null) _preview.Start();
    }

    private Button Action(string key, Action action, bool primary = false)
    {
        var button = MusicWishUi.Button(T(key), "GameSounds." + key, action);
        StyleButton(button, primary); return button;
    }

    private static void StyleButton(Button button, bool primary = false)
    {
        button.SetResourceReference(StyleProperty, primary ? "SettingsPrimaryActionStyle" : "SettingsActionButtonStyle");
        button.Padding = new(7, 3, 7, 3);
        button.Margin = new(0, 2, 4, 2);
        button.MinHeight = 26; button.MinWidth = 0; button.FontSize = 13;
        button.VerticalAlignment = VerticalAlignment.Center;
        if (button.Content is TextBlock text)
            text.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(Foreground)) { Source = button });
    }

    private Button Action(string key, Func<Task> action, bool primary = false) => Action(key, (Action)(async () =>
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { Report(error); }
    }), primary);

    private static TextBlock LabelText(string text, bool heading = false)
    {
        var block = MusicWishUi.Text(text, heading);
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        block.FontSize = heading ? 15 : 13;
        block.Margin = heading ? new(0, 2, 0, 3) : new(0, 1, 0, 2);
        return block;
    }
    private TextBlock Label(string key, bool heading = false)
    {
        var label = LabelText(T(key), heading); label.Tag = key; return label;
    }

    private void TranslateStaticControls(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is Button { Content: TextBlock text } button &&
                AutomationProperties.GetAutomationId(button) is var id && id.StartsWith("GameSounds.", StringComparison.Ordinal))
            {
                var translation = _localize(id);
                if (translation != id) text.Text = translation;
            }
            if (child is TextBlock { Tag: string key } label) label.Text = T(key);
            TranslateStaticControls(child);
        }
    }
    private CheckBox Check(string key, bool value, Action<bool> set)
    {
        var check = MusicWishUi.Check(T(key), value, set);
        check.Style = null; check.MinHeight = 20;
        check.Content = Label(key);
        AutomationProperties.SetAutomationId(check, "GameSounds." + key);
        return check;
    }
    private static WrapPanel Row() => new() { Orientation = Orientation.Horizontal };
    private static ScrollViewer Scroll(UIElement content) => new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private DockPanel ResultSection(string title, UIElement content)
    {
        var section = new DockPanel { Margin = new(0, 0, 0, 6) };
        var heading = Label(title, true);
        DockPanel.SetDock(heading, Dock.Top);
        section.Children.Add(heading); section.Children.Add(Scroll(content));
        return section;
    }
    private static void ThemeInput(System.Windows.Controls.Control control)
    {
        control.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "InputBrush");
        control.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextPrimaryBrush");
        control.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, "LineBrush");
    }
    private static ComboBox Select(IEnumerable<string> items) { var box = new ComboBox { ItemsSource = items }; ThemeInput(box); return box; }
    private static GridSplitter Split(bool column)
    {
        var splitter = new GridSplitter { ResizeBehavior = GridResizeBehavior.PreviousAndNext, ShowsPreview = true,
            ResizeDirection = column ? GridResizeDirection.Columns : GridResizeDirection.Rows,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        splitter.SetResourceReference(BackgroundProperty, "LineBrush");
        return splitter;
    }
    private static void Add(Grid grid, UIElement item, int col = 0, int row = 0)
    { Grid.SetColumn(item, col); Grid.SetRow(item, row); grid.Children.Add(item); }
}
