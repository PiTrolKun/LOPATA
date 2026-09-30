using System.Drawing;
using System.Windows.Forms;

namespace AIHub.Services;

/// <summary>One icon per main window. Menu callbacks are marshalled by the WPF host.</summary>
public sealed class ApplicationTrayController : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon _image;
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _open = new(), _settings = new(), _updates = new(), _exit = new();
    private readonly Func<string, string> _text;
    private bool _disposed;
    public bool IsAvailable => !_disposed && _icon.Visible;

    private ApplicationTrayController(Icon image, Func<string, string> text, Action open, Action settings, Action updates, Action exit)
    {
        _image = image; _text = text;
        _menu.Items.AddRange([_open, _settings, _updates, new ToolStripSeparator(), _exit]);
        _open.Click += (_, _) => open(); _settings.Click += (_, _) => settings();
        _updates.Click += (_, _) => updates(); _exit.Click += (_, _) => exit();
        _icon = new NotifyIcon { Icon = image, Text = "ЛОПАТА / LOPATA", ContextMenuStrip = _menu };
        _icon.DoubleClick += (_, _) => open();
        try { RefreshLocalization(); _icon.Visible = true; }
        catch { _icon.Dispose(); _menu.Dispose(); throw; }
    }

    public static ApplicationTrayController? TryCreate(Func<string, string> text, Action open, Action settings, Action updates, Action exit)
    {
        Icon? image = null;
        try
        {
            using var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/AIHub;component/Assets/AppIcon.ico"))?.Stream;
            image = stream is null ? (Icon)SystemIcons.Application.Clone() : new Icon(stream);
            return new(image, text, open, settings, updates, exit);
        }
        catch (Exception ex)
        {
            image?.Dispose();
            OwnedProcessRegistry.Log("tray_unavailable", "Application", detail: ex.GetType().Name);
            return null;
        }
    }

    public void RefreshLocalization()
    {
        if (_disposed) return;
        _open.Text = _text("Tray.Open"); _settings.Text = _text("Settings.Title");
        _updates.Text = _text("Updates.Title"); _exit.Text = _text("Tray.Exit");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _icon.Visible = false; _icon.Dispose(); _menu.Dispose(); _image.Dispose();
    }
}
