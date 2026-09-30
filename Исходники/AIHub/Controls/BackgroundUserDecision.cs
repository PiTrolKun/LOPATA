using System.Windows;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Controls;

internal static class BackgroundUserDecision
{
    internal static void RequireVisible(FrameworkElement owner)
    {
        if (ApplicationBackgroundOperations.Current is not null && !owner.IsVisible)
            throw new BackgroundOperationWaitingException("Tray.NeedsInput");
    }

    internal static bool? ShowDialog(Window window, FrameworkElement owner, CancellationToken token)
    {
        RequireVisible(owner); token.ThrowIfCancellationRequested();
        using var registration = token.Register(() => window.Dispatcher.BeginInvoke(() =>
        { if (window.IsVisible) window.Close(); }));
        var result = window.ShowDialog();
        token.ThrowIfCancellationRequested(); return result;
    }
}
