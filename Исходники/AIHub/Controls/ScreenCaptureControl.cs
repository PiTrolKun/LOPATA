using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using AIHub.Models;
using UserControl = System.Windows.Controls.UserControl;
using Control = System.Windows.Controls.Control;

namespace AIHub.Controls;

/// <summary>Action clusters configure the same settings as the global Utilities section.</summary>
public sealed class ScreenCaptureControl : UserControl
{
    private Func<string, string> _text = k => k;
    private ScreenCaptureSettings _settings = new();
    private Func<string, string> _bindingStatus = _ => "";
    private readonly StackPanel _panel = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 12) };
    private Grid? _body;
    private UniformGrid? _actions;
    private Border? _parameters;
    private CaptureMode _mode;
    public event Action? Changed;
    public event Action<string>? AssignRequested;
    public event Action? FolderRequested;
    public event Action<Exception>? ErrorReported;
    public event Action? StopRequested;
    public event Action? RecoveryRequested;
    public event Action? VideoRecoveryRequested;
    public ScreenCaptureControl()
    {
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/SettingsResources.xaml", UriKind.Relative) });
        Content = new ScrollViewer { Content = _panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetAutomationId(this, "Capture.Page");
        SizeChanged += (_, _) => ArrangeClusters();
    }
    public void Configure(ScreenCaptureSettings settings, Func<string, string> text, Func<string, string> bindingStatus)
    { _settings = settings; _text = text; _bindingStatus = bindingStatus; Render(); }
    public void Refresh() => Render();
    public void SetStatus(string status) => _status.Text = status;
    private Border Card(string id, StackPanel content)
    {
        var card = new Border { Child = content, Padding = new(18), Margin = new(0, 0, 12, 12), CornerRadius = new(12), BorderThickness = new(1) };
        card.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); card.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        AutomationProperties.SetAutomationId(card, id); return card;
    }
    private void Render()
    {
        var editor = new CaptureSettingsEditor(_settings, _text, () => Changed?.Invoke(),
            command => AssignRequested?.Invoke(command), () => FolderRequested?.Invoke(),
            error => ErrorReported?.Invoke(error), _bindingStatus, Render);
        _panel.Children.Clear(); _panel.Children.Add(CaptureUi.Text(_text("Capture.Title"), true));
        _panel.Children.Add(CaptureUi.Text(_text("Capture.Description")));
        var tabs = new WrapPanel();
        foreach (var mode in Enum.GetValues<CaptureMode>())
        {
            var tab = CaptureUi.Button(_text("Capture.Mode." + mode), () => { _mode = mode; Render(); });
            if (_mode == mode) { tab.FontWeight = FontWeights.Bold; tab.SetResourceReference(Control.BorderBrushProperty, "AccentBrush"); }
            AutomationProperties.SetAutomationId(tab, "Capture.Mode." + mode); tabs.Children.Add(tab);
        }
        _panel.Children.Add(tabs);
        _panel.Children.Add(CaptureUi.Text(_text("Capture.ActionHint")));
        _body = new Grid(); _body.ColumnDefinitions.Add(new()); _body.ColumnDefinitions.Add(new());
        _body.RowDefinitions.Add(new() { Height = GridLength.Auto }); _body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _actions = new UniformGrid { Columns = 2, VerticalAlignment = VerticalAlignment.Top };
        foreach (var source in ScreenCaptureSettings.Sources(_mode))
        {
            var content = new StackPanel(); var title = CaptureUi.Text(_text("Capture.Source." + source), true); title.FontSize = 20; content.Children.Add(title);
            editor.Binding(content, ScreenCaptureSettings.Command(_mode, source));
            if (_mode == CaptureMode.Screenshot) editor.Output(content, source);
            if (source == CaptureSource.Window)
                editor.Toggle(content, "Capture.RestoreHidden", _settings.RestoreHiddenWindow, v => _settings.RestoreHiddenWindow = v);
            if (source == CaptureSource.Area && _mode == CaptureMode.Screenshot)
                editor.Toggle(content, "Capture.Enlarge", _settings.EnlargeArea, v => _settings.EnlargeArea = v);
            _actions.Children.Add(Card("Capture.Action." + _mode + "." + source, content));
        }
        _body.Children.Add(_actions);
        var parameters = new StackPanel(); parameters.Children.Add(CaptureUi.Text(_text("Capture.ModeSettings"), true));
        if (_mode == CaptureMode.Screenshot) editor.Images(parameters);
        else if (_mode == CaptureMode.Gif) editor.Gif(parameters);
        else editor.Video(parameters);
        editor.Toggle(parameters, "Capture.Cursor", _settings.IncludeCursor, v => _settings.IncludeCursor = v);
        if (_mode != CaptureMode.Screenshot) editor.Processing(parameters);
        parameters.Children.Add(CaptureUi.Text(_text("Capture.SharedFolder"))); editor.Folder(parameters);
        if (_mode != CaptureMode.Screenshot)
        {
            parameters.Children.Add(CaptureUi.Text(_text("Capture.Stop"), true)); editor.Binding(parameters, "Stop");
                parameters.Children.Add(CaptureUi.Button(_text("Capture.Stop"), () => StopRequested?.Invoke()));
                parameters.Children.Add(CaptureUi.Button(_text(_mode == CaptureMode.Gif ? "Capture.GifRecover" : "Capture.VideoRecover"), () =>
                { if (_mode == CaptureMode.Gif) RecoveryRequested?.Invoke(); else VideoRecoveryRequested?.Invoke(); }));
        }
        _parameters = Card("Capture.Parameters", parameters); _body.Children.Add(_parameters);
        _panel.Children.Add(_body); _panel.Children.Add(_status); ArrangeClusters();
    }
    private void ArrangeClusters()
    {
        if (_body is null || _actions is null || _parameters is null) return;
        var split = ActualWidth >= 1050;
        _body.ColumnDefinitions[0].Width = new GridLength(3, GridUnitType.Star);
        _body.ColumnDefinitions[1].Width = split ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(_parameters, split ? 1 : 0); Grid.SetRow(_parameters, split ? 0 : 1);
        _actions.Columns = ActualWidth >= 700 ? 2 : 1;
    }
}
