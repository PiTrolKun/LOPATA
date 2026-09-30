namespace AIHub.Services;

/// <summary>Windows notification, including while our window is active. Contains no project data.</summary>
internal static class DesktopAttentionNotification
{
    public static Func<string, string, bool>? Notify { get; set; }
    public static bool Show(string title, string message)
    {
        if (System.Windows.Application.Current is not { } app) return false;
        if (!app.Dispatcher.CheckAccess()) return app.Dispatcher.Invoke(() => Show(title, message));
        return Notify?.Invoke(title, message) == true;
    }
}
