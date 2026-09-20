using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using UserControl = System.Windows.Controls.UserControl;

namespace AIHub.Controls;

public sealed partial class LiteraryStudioControl : UserControl
{
    partial void EditPrompts();
    private readonly string _directory, _language;
    private readonly Func<string,string> _l;
    private readonly Func<bool> _blocked;
    private readonly LiteraryChatRuntime _runtime;
    private readonly ILiteraryStudioRequests _requests;
    private readonly LiteraryDraftControl _draft;
    private readonly LiteraryStudioStore _store;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly TextBox _input = LiteraryWorkspaceParts.TextArea(false);
    private readonly StackPanel _messages = new(), _quotes = new();
    private readonly Grid _actions = new() { VerticalAlignment = VerticalAlignment.Top };
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _status = LiteraryUi.Text(""), _tokens = LiteraryUi.Text(""), _inputLabel = LiteraryUi.Text(""), _receipts = LiteraryUi.Text("");
    private readonly TextBlock _role = LiteraryUi.Text("");
    private readonly Button _send = new(), _sendWriter = new(), _stop = new(), _clear = new(), _archive = new();
    private readonly LiteraryRequestIndicator _activity = new();
    private readonly ComboBox _route = new();
    private readonly ContentControl _sources = new();
    private readonly TextBlock _routeDescription = LiteraryUi.Text("");
    private readonly List<Button> _buttons = [];
    private LiteraryParagraphCatalog _catalog = null!;
    private LiteraryParagraphTree _tree = null!;
    private LiteraryFreeChatWindow? _freeChat;
    private CancellationTokenSource? _operation;
    private bool _dirty, _showArchive, _loading;
    public LiteraryStudioState State { get; }
    public System.Windows.FrameworkElement StatusContent { get; private set; } = null!;
    public bool IsWorking => _operation is not null;
    // Future task-review surface plugs in here, without reinstating the old mandatory edit screen.
    public Func<string, Task<string?>>? ReviewTaskAsync { get; set; }

    public LiteraryStudioControl(string directory, LiteraryDraftControl draft, ContentControl editor,
        LiteraryChatRuntime runtime, Func<bool> blocked, Func<string,string> l, string language, UIElement memoryStatus, UIElement navigation,
        ILiteraryStudioRequests? requests = null)
    {
        _directory = directory; _draft = draft; _runtime = runtime; _requests = requests ?? runtime; _blocked = blocked; _l = l; _language = language;
        _store = new(new(directory)); State = _store.Load();
        BuildLayout(editor, memoryStatus, navigation); RefreshSources(); Render();
        ConfigureTransferSettings();
        if (State.Interrupted) { _status.Text = l("Paragraph.Interrupted"); State.Interrupted = false; _dirty = true; }
        _input.TextChanged += (_,_) => { if (_loading) return; State.Input = _input.Text; _dirty = true; StopHint(); Availability(); };
        _input.PreviewKeyDown += InputKeyDown;
        _send.Click += async (_,_) => await SendToAdvisorAsync();
        _sendWriter.Click += async (_,_) => await SendToWriterAsync();
        _stop.Click += (_,_) => _operation?.Cancel();
        _clear.Click += (_,_) => { State.Clear(); ClearRequestStatus(); _showArchive = false; Render(); Save(); };
        _archive.Click += (_,_) => { _showArchive = !_showArchive; RenderMessages(); };
        _route.SelectionChanged += (_,_) =>
        {
            if (_loading) return; State.RouteId = (_route.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
            RouteDescription(); _dirty = true;
        };
        _timer.Tick += (_,_) => { Availability(); if (_dirty) Save(); };
        Loaded += (_,_) => _timer.Start(); Unloaded += (_,_) => { _timer.Stop(); Save(); };
    }
    public bool Save()
    {
        try { _store.Save(State); _dirty = false; return true; }
        catch (Exception ex) { _status.Text = _l("Paragraph.SaveError") + " " + ex.Message; return false; }
    }
    public bool CanLeave()
    {
        if (_operation is not null || _freeChat?.IsWorking == true) { _status.Text = _l("Studio.StopFirst"); return false; }
        if (!Save()) return false;
        _freeChat?.Close(); return true;
    }
    public LiteraryFreeChatWindow? ReleaseFreeChat()
    { var window=_freeChat; _freeChat=null; return window; }
    public void AdoptFreeChat(LiteraryFreeChatWindow? window)
    { _freeChat=window; if(window is not null) window.Closed+=(_,_)=>_freeChat=null; }
    public void AttachQuote(string source, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || IsWorking) return;
        State.Quotes.Add(new(source,text)); _dirty = true; RenderQuotes();
    }
    public void RefreshSources()
    {
        var project = LiteraryProjectStore.ReadProject(_directory);
        _catalog = new(project, _draft.Capture(project.Id, _directory), _l);
        var selection = new LiteraryParagraphState { Selection = State.Selection, Recommendations = State.Recommendations };
        _tree = new(_catalog, selection, _l, _language); _tree.Changed += () => _dirty = true; _sources.Content = _tree;
        _loading = true; _route.Items.Clear(); _route.Items.Add(new ComboBoxItem { Content = _l("Paragraph.NoRoute"), Tag = "" });
        foreach (var node in _catalog.Roots.Single(r=>r.Id=="route").Children)
            _route.Items.Add(new ComboBoxItem { Content = node.Label, Tag = node.Id });
        if (State.RouteId.Length > 0 && !_catalog.Routes.ContainsKey(State.RouteId))
            _route.Items.Add(new ComboBoxItem { Content = _l("Paragraph.RouteMissing"), Tag = State.RouteId });
        _route.SelectedItem = _route.Items.OfType<ComboBoxItem>().FirstOrDefault(i=>(string)i.Tag==State.RouteId) ?? _route.Items[0];
        _loading = false; RouteDescription(); ClearRequestStatus();
    }
    private void RouteDescription() => _routeDescription.Text = State.RouteId.Length == 0 ? _l("Paragraph.NoRoute")
        : _catalog.Routes.TryGetValue(State.RouteId, out var route) ? _l("Paragraph.RouteBoundary") + " " + route.Description : _l("Paragraph.RouteMissing");
    private void Render()
    {
        _loading = true; _input.Text = State.Input; _loading = false;
        _role.Text = _l("Studio.Role." + State.Role);
        _role.ToolTip = _l("Studio.TransferSettingsHint");
        _input.ToolTip = $"Enter — {_l("Studio.KeyAction." + State.EnterAction)}; Ctrl+Enter — {_l("Studio.KeyAction." + State.ControlEnterAction)}. " + _l("Studio.SendInputHint");
        _send.ToolTip = _l("Studio.SendAdvisor") + "\n" + _l("Studio.TransferSettingsHint");
        _sendWriter.ToolTip = _l(State.Role == LiteraryChatProfile.Advisor
            ? State.DirectRequest ? "Studio.ToWriterDirect" : "Studio.ToWriter" : "Studio.SendWriter")
            + "\n" + _l("Studio.TransferSettingsHint");
        BuildActions(); RenderMessages(); RenderQuotes(); Availability();
    }
    private void Availability()
    {
        var ready = _operation is null && !_blocked();
        foreach (var button in _buttons) button.IsEnabled = ready;
        _stop.IsEnabled = _operation is not null; _input.IsReadOnly = !ready;
        _sources.IsEnabled = _route.IsEnabled = ready;
        _send.IsEnabled = ready;
        _sendWriter.IsEnabled = ready && CanSendToWriter;
        _send.Opacity = _send.IsEnabled ? 1 : .4; _sendWriter.Opacity = _sendWriter.IsEnabled ? 1 : .4;
        _clear.IsEnabled = ready; _inputLabel.Text = (State.WriterComment ? _l("Studio.Comment") + " · " : "") + _l("Studio.Action." + State.Action)
            + (State.Role == LiteraryChatProfile.Writer && State.Action == "Continue" ? " · " + _l(State.ContinueFromChat ? "Studio.FromChat" : "Studio.FromEditor") : "");
    }
    private Button ActionButton(string label, Action click, bool primary = false)
    {
        var button = LiteraryUi.Button(label, click); Compact(button, primary); _buttons.Add(button); return button;
    }
    private static void Compact(Button button, bool primary = false)
    {
        button.SetResourceReference(StyleProperty, primary ? "PrimaryButtonStyle" : "SecondaryButtonStyle");
        button.MinWidth = 0; button.MinHeight = 30; button.Padding = new Thickness(10,6,10,6);
        button.FontSize = 14; button.Margin = new Thickness(0,0,5,5);
    }
}
