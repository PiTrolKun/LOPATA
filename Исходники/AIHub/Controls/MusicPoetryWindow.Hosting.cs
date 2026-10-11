using System.Windows;
using System.Windows.Controls;

namespace AIHub.Controls;

// Reuse the session and editor view across both entry points; never show a second chat.
public sealed partial class MusicPoetryWindow
{
    private readonly DockPanel _view;
    private ContentControl? _embeddedHost;
    private bool _viewInitialized, _viewClosed;
    private Window DialogOwner => Window.GetWindow(_view) ?? this;

    public void PresentIn(ContentControl host)
    {
        if (_viewClosed) return;
        if (!CloseHostDialogs()) return;
        if (IsVisible) Hide();
        DetachView(); Content = null;
        // The editor retains its themed resources when it leaves the Window's logical tree.
        if (!_view.Resources.MergedDictionaries.Contains(Resources)) _view.Resources.MergedDictionaries.Add(Resources);
        _embeddedHost = host; host.Content = _view;
        if (_viewInitialized && _view.IsLoaded) BeginModelEntry();
    }

    public void PresentAsWindow()
    {
        if (_viewClosed) return;
        if (!CloseHostDialogs()) return;
        DetachView(); Content = _view;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show(); Activate();
        if (_viewInitialized && _view.IsLoaded) BeginModelEntry();
    }

    private void DetachView()
    {
        if (_embeddedHost is { } host && ReferenceEquals(host.Content, _view)) host.Content = null;
        _embeddedHost = null;
    }

    private bool CloseHostDialogs()
    {
        if (_modelSelector?.SaveForExit() == false) return false;
        CancelModelEntry();
        _modelSelector?.Close(); _sessionManager?.Close();
        foreach (Window window in DialogOwner.OwnedWindows.Cast<Window>().ToArray())
            if (ReferenceEquals(window.Resources, Resources)) window.Close();
        return true;
    }
}
