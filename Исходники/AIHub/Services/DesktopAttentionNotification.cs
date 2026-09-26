using System.Windows.Threading;
using Application = System.Windows.Application;
using WindowState = System.Windows.WindowState;

namespace AIHub.Services;

/// <summary>Windows notification, including while our window is active. Contains no project data.</summary>
internal sealed class DesktopAttentionNotification : IDisposable
{
    private static DesktopAttentionNotification? _instance;
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly DispatcherTimer _expiry;
    private bool _disposed;

    private DesktopAttentionNotification()
    {
        _icon = new() { Text = "ЛОПАТА", Icon = System.Drawing.SystemIcons.Warning };
        _icon.BalloonTipClicked += (_, _) => Activate();
        _icon.MouseClick += (_, _) => Activate();
        _expiry = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(2) };
        _expiry.Tick += (_, _) => { _expiry.Stop(); _icon.Visible = false; };
        Application.Current.Exit += (_, _) => Dispose();
    }

    public static bool Show(string title, string message)
    {
        if (Application.Current is not { } app) return false;
        if (!app.Dispatcher.CheckAccess()) return app.Dispatcher.Invoke(() => Show(title, message));
        _instance ??= new();
        _instance._icon.Visible = true;
        _instance._icon.ShowBalloonTip(15000, title, message, System.Windows.Forms.ToolTipIcon.Warning);
        _instance._expiry.Stop(); _instance._expiry.Start();
        return true;
    }

    private void Activate()
    {
        if (Application.Current.MainWindow is { } main)
        { main.Show(); if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal; main.Activate(); }
        _expiry.Stop(); _icon.Visible = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _expiry.Stop(); _icon.Dispose(); _instance = null;
    }
}
