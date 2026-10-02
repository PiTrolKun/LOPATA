using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using AIHub.Models;
using UserControl = System.Windows.Controls.UserControl;

namespace AIHub.Controls;

public sealed class ScreenCaptureSettingsControl : UserControl
{
    private Func<string, string> _text = k => k;
    private ScreenCaptureSettings _settings = new();
    private readonly StackPanel _panel = new();
    private Func<string, string> _bindingStatus = _ => "";
    public event Action? Changed;
    public event Action<string>? AssignRequested;
    public event Action? FolderRequested;
    public event Action<Exception>? ErrorReported;
    public ScreenCaptureSettingsControl() { Content = _panel; AutomationProperties.SetAutomationId(this, "Capture.Settings"); }
    public void Configure(ScreenCaptureSettings settings, Func<string, string> text, Func<string, string> bindingStatus)
    { _settings = settings; _text = text; _bindingStatus = bindingStatus; Render(); }
    public void Refresh() => Render();
    private void Render()
    {
        var editor = new CaptureSettingsEditor(_settings, _text, () => Changed?.Invoke(),
            command => AssignRequested?.Invoke(command), () => FolderRequested?.Invoke(),
            error => ErrorReported?.Invoke(error), _bindingStatus, Render);
        _panel.Children.Clear();
        editor.Folder(_panel);
        editor.Toggle(_panel, "Capture.RestoreHidden", _settings.RestoreHiddenWindow, v => _settings.RestoreHiddenWindow = v);
        editor.Toggle(_panel, "Capture.Cursor", _settings.IncludeCursor, v => _settings.IncludeCursor = v);
        _panel.Children.Add(CaptureUi.Text(_text("Capture.Mode.Screenshot"), true)); editor.Images(_panel);
        editor.Toggle(_panel, "Capture.Enlarge", _settings.EnlargeArea, v => _settings.EnlargeArea = v);
        foreach (var source in Enum.GetValues<CaptureSource>())
        { _panel.Children.Add(CaptureUi.Text(_text("Capture.Source." + source))); editor.Output(_panel, source); }
        _panel.Children.Add(CaptureUi.Text(_text("Capture.Mode.Gif"), true)); editor.Gif(_panel);
        _panel.Children.Add(CaptureUi.Text(_text("Capture.Mode.Video"), true)); editor.Video(_panel); editor.Processing(_panel);
        _panel.Children.Add(CaptureUi.Text(_text("Capture.Bindings"), true));
        _panel.Children.Add(CaptureUi.Text(_text("Capture.BindingLimits")));
        foreach (var mode in Enum.GetValues<CaptureMode>()) foreach (var source in ScreenCaptureSettings.Sources(mode))
        {
            _panel.Children.Add(CaptureUi.Text(_text("Capture.Mode." + mode) + " · " + _text("Capture.Source." + source)));
            editor.Binding(_panel, ScreenCaptureSettings.Command(mode, source));
        }
        _panel.Children.Add(CaptureUi.Text(_text("Capture.Stop"))); editor.Binding(_panel, "Stop");
    }
}
