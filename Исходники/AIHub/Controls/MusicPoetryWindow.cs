using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using RichTextBox = System.Windows.Controls.RichTextBox;
using Orientation = System.Windows.Controls.Orientation;

namespace AIHub.Controls;

public sealed partial class MusicPoetryWindow : Window
{
    private readonly Func<string, string> _l;
    private readonly Func<MusicProjectSnapshot> _seed;
    private readonly StorageSettings _storage;
    private readonly MusicPoetrySessions _store;
    private readonly IMusicPoetryRuntime _runtime;
    private readonly BackgroundOperationController? _background;
    private MusicPoetrySession _session;
    private DebugModelInfo? _model;
    private DebugModelInfo? _coreModel;
    private readonly TextBox _lyrics = Area("Lyrics"), _parameters = Area("Parameters"), _input = Area("Input"), _guidance = Area("Guidance", true);
    private readonly RichTextBox _chat = new() { IsReadOnly = true, IsDocumentEnabled = false, BorderThickness = new(0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly ComboBox _target = new() { MinHeight = 32, Margin = new(0, 4, 0, 8) };
    private readonly TextBlock _status = Label(""), _lyricsStep = Label(""), _parameterStep = Label("");
    private readonly System.Windows.Controls.ProgressBar _memory = new() { Width = 200, Height = 10, Minimum = 0, Maximum = 100 };
    private readonly TextBlock _memoryIcon = Label("🧠");
    private readonly Button _send, _assemble, _models, _sessions, _new, _partial, _lyricsPrevious, _lyricsNext, _parameterPrevious, _parameterNext;
    private readonly TextBlock _memoryCount = Label("");
    private MusicPoetryTokenPreview? _tokenPreview;
    private int _previewVersion;
    private Window? _sessionManager;
    private readonly DispatcherTimer _save = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private CancellationTokenSource? _operation;
    private bool _loading, _closing, _saveFailed;
    private string _zone = "";
    private int _capacity = CoreContextRuntimeLimits.CurrentBackendContextLimit;
    public bool IsWorking => _operation is not null;
    public event Action? WorkingChanged;

    public MusicPoetryWindow(Func<MusicProjectSnapshot> seed, StorageSettings storage, UserContextService context,
        Func<string, string> localize, string language, MusicPoetrySessions? store = null, IMusicPoetryRuntime? runtime = null)
    {
        _seed = seed; _storage = storage; _l = localize; _store = store ?? MusicPoetrySessions.Default;
        _runtime = runtime ?? new MusicPoetryRuntime(context); _background = ApplicationBackgroundOperations.Current;
        _session = MusicPoetryProtocol.Create(seed(), localize);
        Title = L("Title"); Width = 1380; Height = 870; MinWidth = 900; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (System.Windows.Application.Current?.MainWindow is { } main) Resources.MergedDictionaries.Add(main.Resources);
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/SettingsResources.xaml", UriKind.Relative) });
        LiterarySpellChecking.ApplyMenuTheme(Resources);
        SetResourceReference(FontSizeProperty, "UiBodyFontSize"); UseLayoutRounding = true;
        _target.SetResourceReference(StyleProperty, typeof(ComboBox));
        _target.ItemTemplate = new DataTemplate { VisualTree = TargetTemplate() };
        var root = new DockPanel { Margin = new(12) }; Content = root;
        var header = new WrapPanel { Margin = new(0, 0, 0, 12), HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        _sessions = Action(header, "Sessions", OpenSessions);
        _new = Action(header, "New", NewSession);
        _partial = Action(header, "Partial", ViewPartial);
        _models = Action(header, "Models", ModelMenu); _models.Content = "ИИ±";
        Action(header, "Help", () => ShowText(L("Help"), MusicPoetryProtocol.Instructions, "Protocol"));
        var footer = new DockPanel { Margin = new(0, 10, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var meter = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        meter.Children.Add(_memoryIcon); meter.Children.Add(_memoryCount); meter.Children.Add(_memory); DockPanel.SetDock(meter, Dock.Right); footer.Children.Add(meter); footer.Children.Add(_status);
        AutomationProperties.SetAutomationId(_memory, "Music.Poetry.Context");
        var columns = new Grid(); root.Children.Add(columns);
        foreach (var width in new[] { 1.0, 0, 1.4, 0, 1.05 }) columns.ColumnDefinitions.Add(new() {
            Width = width == 0 ? new(8) : new(width, GridUnitType.Star), MinWidth = width == 0 ? 8 : 200 });
        var song = new DockPanel(); var songHead = new DockPanel(); DockPanel.SetDock(songHead, Dock.Top); song.Children.Add(songHead);
        _lyricsPrevious = Action(songHead, "Previous", () => Navigate(false, -1), "‹");
        _lyricsNext = Action(songHead, "Next", () => Navigate(false, 1), "›"); songHead.Children.Add(_lyricsStep);
        var songTitle = Label(L("Song")); DockPanel.SetDock(songTitle, Dock.Top); song.Children.Add(songTitle); song.Children.Add(_lyrics);
        Add(Card(song), 0); Splitter(1);
        var conversation = new Grid(); conversation.RowDefinitions.Add(new()); conversation.RowDefinitions.Add(new() { Height = new(8) });
        conversation.RowDefinitions.Add(new() { Height = new(145), MinHeight = 90 });
        _chat.SetResourceReference(BackgroundProperty, "PanelBrush"); _chat.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        AutomationProperties.SetAutomationId(_chat, "Music.Poetry.Chat"); conversation.Children.Add(_chat);
        var resizeInput = new GridSplitter { Height = 8, HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch, ResizeDirection = GridResizeDirection.Rows };
        resizeInput.SetResourceReference(BackgroundProperty, "LineBrush"); Grid.SetRow(resizeInput, 1); conversation.Children.Add(resizeInput);
        var entry = new DockPanel(); var entryActions = new WrapPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        DockPanel.SetDock(entryActions, Dock.Bottom); entry.Children.Add(entryActions);
        _assemble = Action(entryActions, "Assemble", () => _ = SendCoreAsync(true));
        var composer = new Grid(); composer.Children.Add(_input);
        _input.AcceptsReturn = false; _input.AcceptsTab = false; _input.Padding = new(8, 8, 54, 8);
        _send = Action(composer, "Send", () => _ = SendAsync(), "➤");
        _send.Width = 38; _send.Height = 38; _send.Padding = new(0); _send.FontSize = 20;
        _send.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
        _send.VerticalAlignment = VerticalAlignment.Bottom; _send.Margin = new(8);
        AutomationProperties.SetName(_send, L("Send"));
        entry.Children.Add(composer); Grid.SetRow(entry, 2); conversation.Children.Add(entry); Add(Card(conversation), 2); Splitter(3);
        var data = new DockPanel(); var dataHead = new DockPanel(); DockPanel.SetDock(dataHead, Dock.Top); data.Children.Add(dataHead);
        _parameterPrevious = Action(dataHead, "PreviousParameters", () => Navigate(true, -1), "‹");
        _parameterNext = Action(dataHead, "NextParameters", () => Navigate(true, 1), "›"); dataHead.Children.Add(_parameterStep);
        var targetTitle = Label(L("Target")); DockPanel.SetDock(targetTitle, Dock.Top); data.Children.Add(targetTitle);
        _target.ItemsSource = MusicPoetryProtocol.Variations.Select(id => new Target(id, MusicModelVariants.Name(id))).ToArray();
        _target.SelectedValuePath = "Id"; DockPanel.SetDock(_target, Dock.Top); data.Children.Add(_target);
        var guides = new DockPanel(); var guideTitle = Label(L("Guidance")); DockPanel.SetDock(guideTitle, Dock.Top); guides.Children.Add(guideTitle);
        _guidance.MinHeight = 0; _guidance.Height = 115; guides.Children.Add(_guidance); DockPanel.SetDock(guides, Dock.Bottom); data.Children.Add(guides);
        data.Children.Add(_parameters); Add(Card(data), 4);
        LiterarySpellChecking.Enable(_lyrics, language); LiterarySpellChecking.Enable(_input, language);
        foreach (var (input, zone) in new[] { (_lyrics, "song"), (_parameters, "parameters"), (_input, "chat") })
        {
            input.GotKeyboardFocus += (_, _) => Activity(zone);
            input.TextChanged += (_, _) => Edited();
        }
        _target.GotKeyboardFocus += (_, _) => Activity("parameters");
        _target.SelectionChanged += (_, _) => { if (!_loading) { Edited(); _guidance.Text = MusicPoetryProtocol.Guidance(_session.Parameters.Variation, _l); } };
        _input.PreviewKeyDown += InputKeyDown;
        _save.Tick += (_, _) => { _save.Stop(); Persist(); };
        Closing += ClosingWindow;
        Closed += (_, _) => { ++_previewVersion; _save.Stop(); if (_background is not null) _background.Changed -= BackgroundChanged; _runtime.Dispose(); };
        if (_background is not null) _background.Changed += BackgroundChanged;
        Loaded += async (_, _) => { try { var models = await Task.Run(() => MusicPoetryRuntime.Discover(_storage));
                _coreModel = models.FirstOrDefault(m => m.IsCoreModel);
                if (_model is null) _model = string.IsNullOrEmpty(_session.ModelPath) ? models.FirstOrDefault(m => m.IsCoreModel)
                    : models.FirstOrDefault(m => m.Path.Equals(_session.ModelPath, StringComparison.OrdinalIgnoreCase));
                ModelCaption(); Recount(); if (_model is null) _status.Text = L("ModelMissing"); }
            catch (Exception ex) { _status.Text = L("ModelMissing") + " " + ex.Message; } Availability(); };
        LoadSession(); Persist();
        void Add(UIElement child, int column) { Grid.SetColumn(child, column); columns.Children.Add(child); }
        void Splitter(int column) { var s = new GridSplitter { Width = 8, HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            ResizeDirection = GridResizeDirection.Columns }; s.SetResourceReference(BackgroundProperty, "LineBrush"); Add(s, column); }
    }
    private sealed record Target(string Id, string Name);
    private static FrameworkElementFactory TargetTemplate()
    {
        var text = new FrameworkElementFactory(typeof(TextBlock)); text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Name"));
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); return text;
    }
    private string L(string key) => _l("Music.Poetry." + key);
    private static TextBlock Label(string value) { var t = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new(4) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush"); t.SetResourceReference(TextBlock.FontSizeProperty, "UiBodyFontSize"); return t; }
    private static TextBox Area(string id, bool readOnly = false)
    {
        var input = new TextBox { IsReadOnly = readOnly, AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 25, Padding = new(8), IsUndoEnabled = true, UndoLimit = 1000 };
        input.SetResourceReference(BackgroundProperty, "PanelBrush"); input.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        input.SetResourceReference(TextBox.CaretBrushProperty, "TextPrimaryBrush"); input.SetResourceReference(TextBox.BorderBrushProperty, "LineBrush");
        input.SetResourceReference(TextBox.FontSizeProperty, "UiBodyFontSize");
        AutomationProperties.SetAutomationId(input, "Music.Poetry." + id); return input;
    }
    private Button Action(System.Windows.Controls.Panel parent, string key, Action action, string? caption = null)
    {
        var b = new Button { Content = caption ?? L(key), ToolTip = L(key), Margin = new(3), Padding = new(8, 5, 8, 5), MinWidth = 32 };
        b.SetResourceReference(StyleProperty, "SettingsActionButtonStyle"); AutomationProperties.SetAutomationId(b, "Music.Poetry." + key);
        b.Click += (_, _) => action(); parent.Children.Add(b); return b;
    }
    private static Border Card(UIElement child) { var b = new Border { Child = child, CornerRadius = new(10), Padding = new(8), BorderThickness = new(1) };
        b.SetResourceReference(BackgroundProperty, "PanelBrush"); b.SetResourceReference(Border.BorderBrushProperty, "LineBrush"); return b; }
}
