using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;

namespace AIHub.Controls;

public sealed partial class SongClipControl
{
    private Uri? _selectedPreviewSource;
    private Window? _expandedPreviewWindow;
    private MediaElement? _expandedPreviewMedia;

    private void ShowResult()
    {
        SuspendPreview();
        _selectedPreviewSource = SelectedResult() is { } result
            ? new Uri(_store.Resolve(_project.Id, result.File)) : null;
    }

    private void TogglePreviewPlayback()
    {
        if (_selectedPreviewSource is null) return;
        CloseExpandedPreview();
        if (_playing)
        {
            _preview.IsMuted = true;
            _preview.Pause();
            _playing = false;
            return;
        }

        // Navigation releases the decoder; only an explicit click reopens the selected result.
        _preview.Source ??= _selectedPreviewSource;
        _preview.IsMuted = false;
        _preview.Play();
        _playing = true;
    }

    private void OpenExpandedPreview()
    {
        if (_expandedPreviewWindow is { } existing)
        {
            existing.Activate();
            return;
        }
        if (_selectedPreviewSource is not { } source) return;

        ReleasePreviewMedia(_preview);
        _playing = false;
        var media = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Close,
            Stretch = Stretch.Uniform,
            Source = source
        };
        var window = new Window
        {
            Owner = Window.GetWindow(this),
            Content = media,
            WindowState = WindowState.Maximized,
            Background = Brushes.Black
        };
        AutomationProperties.SetAutomationId(media, "SongClip.Player.Media");
        AutomationProperties.SetAutomationId(window, "SongClip.Player.Window");
        _expandedPreviewMedia = media;
        _expandedPreviewWindow = window;
        // Closing runs before the native window is destroyed. The Manual mode permits
        // transport commands; automatic Play/Close previously made Stop throw here.
        window.Closing += (_, _) => ReleasePreviewMedia(media);
        window.Closed += (_, _) =>
        {
            // Closing can be skipped for owned windows when their owner closes.
            ReleasePreviewMedia(media);
            if (ReferenceEquals(_expandedPreviewWindow, window))
            {
                _expandedPreviewWindow = null;
                _expandedPreviewMedia = null;
            }
            window.Content = null;
        };
        window.Show();
        media.Play();
    }

    private void CloseExpandedPreview()
    {
        if (_expandedPreviewMedia is { } media) media.IsMuted = true;
        _expandedPreviewWindow?.Close();
    }

    private void SuspendPreview()
    {
        CloseExpandedPreview();
        ReleasePreviewMedia(_preview);
        _playing = false;
    }

    private static void ReleasePreviewMedia(MediaElement media)
    {
        // WPF queues native media operations. Mute first so pending open/play callbacks
        // cannot produce a short sound while Stop/Close and URI removal are processed.
        media.IsMuted = true;
        media.Stop();
        media.Close();
        media.Source = null;
    }
}
