using System.Windows;
using System.Windows.Controls;
using AIHub.Services;
using AIHub.Services.VideoEditor;
using AIHub.Services.VectorGraphics;

namespace AIHub.Controls.VideoEditor;

internal sealed partial class VideoEditorWindow
{
    private Window? _svgProgress;
    private VectorLiveState? _svgLive;
    private VectorLivePreview? _svgPreview;
    private void SvgChanged(string operation, VectorStreamChunk chunk)
    {
        if (_closed || operation != _operationId) return;
        _ = Dispatcher.InvokeAsync(() => {
            if (_closed || !_busy || operation != _operationId) return;
            if (_svgProgress is null)
            {
                _svgLive = new(); _svgPreview = new(_svgLive, key => _l("Vector." + key));
                var panel = new DockPanel { Margin = new(16) }; var controls = new WrapPanel();
                controls.Children.Add(B("Pause", () => Safe(async () => { if (ApplicationBackgroundOperations.Current is {} c && c.State?.Kind == VideoWork.Kind) await c.PauseAsync(); })));
                controls.Children.Add(B("Resume", () => Safe(async () => { if (ApplicationBackgroundOperations.Current is {} c && c.State?.Kind == VideoWork.Kind) await c.ResumeAsync(_lifetime.Token); })));
                DockPanel.SetDock(controls, Dock.Bottom); panel.Children.Add(controls); panel.Children.Add(_svgPreview);
                _svgProgress = new Window { Owner = this, Title = T("GenerateSvg"), Width = 680, Height = 570, MinWidth = 420, MinHeight = 340, Content = panel, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                _svgProgress.Resources.MergedDictionaries.Add(Resources);
                _svgProgress.SetResourceReference(BackgroundProperty, "WindowBackgroundBrush"); _svgProgress.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
                _svgProgress.Closing += (_, e) => { if (_busy && !_closed) { e.Cancel = true; _svgProgress?.Hide(); } };
                _svgProgress.Show();
            }
            _svgLive!.Append(chunk);
        });
    }
    private void CloseSvgProgress()
    {
        _svgPreview?.Dispose(); _svgPreview = null; _svgLive = null;
        var window = _svgProgress; _svgProgress = null;
        if (window is not null) { var busy = _busy; _busy = false; window.Close(); _busy = busy; }
    }
}
