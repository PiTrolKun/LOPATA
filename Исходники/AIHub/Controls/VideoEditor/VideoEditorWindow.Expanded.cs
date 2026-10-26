using System.Windows;
using System.Windows.Controls;

namespace AIHub.Controls.VideoEditor;

internal sealed partial class VideoEditorWindow
{
    private Grid _workspace = null!, _expandedHost = null!;
    private Border _previewFrame = null!;
    private DockPanel _previewDock = null!;
    internal bool PreviewExpanded { get; private set; }
    internal async Task SetPreviewExpandedAsync(bool expanded)
    {
        if (PreviewExpanded == expanded) return;
        PreviewExpanded = expanded;
        // Keep the same WebView, clock, media elements and audio graph alive across layout changes.
        if (expanded) { _previewDock.Children.Remove(_previewFrame); _expandedHost.Children.Add(_previewFrame); }
        else { _expandedHost.Children.Remove(_previewFrame); _previewDock.Children.Add(_previewFrame); }
        _workspace.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        _expandedHost.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        await _preview.SetExpandedAsync(expanded, T(expanded ? "CollapsePreview" : "ExpandPreview"));
    }
}
