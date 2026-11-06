using System.Windows;
using System.Windows.Input;

namespace AIHub.Controls;

public sealed partial class LivingArcadeControl
{
    private bool _pauseSpaceDown;
    private Window? _pauseKeyWindow;

    private void InitializePauseShortcut()
    {
        PreviewKeyDown += (_, args) =>
        {
            if (HandlePauseShortcut(args.Key, args.IsRepeat, false, args.OriginalSource as DependencyObject,
                Window.GetWindow(this)?.IsActive == true, Keyboard.Modifiers)) args.Handled = true;
        };
        PreviewKeyUp += (_, args) =>
        {
            if (HandlePauseShortcut(args.Key, false, true, args.OriginalSource as DependencyObject,
                Window.GetWindow(this)?.IsActive == true, Keyboard.Modifiers)) args.Handled = true;
        };
        Loaded += (_, _) =>
        {
            DetachPauseShortcutWindow();
            _pauseKeyWindow = Window.GetWindow(this);
            if (_pauseKeyWindow is not null) _pauseKeyWindow.Deactivated += ResetPauseShortcut;
        };
        Unloaded += (_, _) => DetachPauseShortcutWindow();
    }

    // The explicit window state is the platform boundary; the command still uses the real page/host state.
    internal bool HandlePauseShortcut(Key key, bool isRepeat, bool keyUp, DependencyObject? source,
        bool windowActive, ModifierKeys modifiers)
    {
        if (key != Key.Space) return false;
        var fromField = PauseSourceWithin(_host, source);
        var fromPauseButton = PauseSourceWithin(_gamePauseButton, source) || PauseSourceWithin(_gameResumeButton, source);
        if (keyUp)
        {
            _pauseSpaceDown = false;
            return _playing && (fromField || fromPauseButton);
        }
        if (_disposed || !_playing || _transitioning || !IsVisible || !windowActive) return false;
        // A focused pause icon must not receive WPF's separate Space-to-click action.
        if (fromPauseButton) return true;
        if (!fromField || modifiers != ModifierKeys.None) return false;
        if (_pauseSpaceDown || isRepeat) return true;
        _pauseSpaceDown = true;
        if (_paused) ResumeGame(); else PauseGame();
        return true;
    }

    private void ResetPauseShortcut(object? sender, EventArgs args) => _pauseSpaceDown = false;

    private static bool PauseSourceWithin(FrameworkElement? element, DependencyObject? source) => element is not null
        && source is not null && (ReferenceEquals(element, source)
            || source is System.Windows.Media.Visual && element.IsAncestorOf(source));

    private void DetachPauseShortcutWindow()
    {
        if (_pauseKeyWindow is not null) _pauseKeyWindow.Deactivated -= ResetPauseShortcut;
        _pauseKeyWindow = null;
        _pauseSpaceDown = false;
    }
}
