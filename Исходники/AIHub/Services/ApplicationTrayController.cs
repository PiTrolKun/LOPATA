using System.Drawing;
using System.Windows.Forms;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>One icon per main window. Menu callbacks are marshalled by the WPF host.</summary>
public sealed class ApplicationTrayController : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon _image;
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _open = new(), _settings = new(), _updates = new(), _exit = new();
    private readonly ToolStripMenuItem _operation = new();
    private readonly ToolStripMenuItem _result = new();
    private ToolStripMenuItem? _captureFolder, _captureMonitor, _captureStop;
    private readonly ToolStripSeparator _operationSeparator = new();
    private Icon? _markedImage;
    private BackgroundOperationPhase? _phase;
    private int? _countdown;
    private int _attention = -1;
    private Action? _balloonAction;
    private readonly Func<string, string> _text;
    private bool _disposed;
    public bool IsAvailable => !_disposed && _icon.Visible;

    private ApplicationTrayController(Icon image, Func<string, string> text, Action open, Action settings, Action updates, Action exit, Action operation, Action result)
    {
        _image = image; _text = text;
        _menu.Items.AddRange([_open, _result, _settings, _updates, _operationSeparator, _operation, new ToolStripSeparator(), _exit]);
        _open.Click += (_, _) => open(); _settings.Click += (_, _) => settings();
        _updates.Click += (_, _) => updates(); _exit.Click += (_, _) => exit();
        _operation.Click += (_, _) => operation();
        _result.Visible = false; _result.Click += (_, _) => result();
        _icon = new NotifyIcon { Icon = image, Text = "ЛОПАТА / LOPATA", ContextMenuStrip = _menu };
        _icon.DoubleClick += (_, _) => open();
        _icon.BalloonTipClicked += (_, _) => (_balloonAction ?? open)();
        try { RefreshLocalization(); _icon.Visible = true; }
        catch { _icon.Dispose(); _menu.Dispose(); throw; }
    }

    public static ApplicationTrayController? TryCreate(Func<string, string> text, Action open, Action settings, Action updates, Action exit, Action? operation = null, Action? result = null)
    {
        Icon? image = null;
        try
        {
            using var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/AIHub;component/Assets/AppIcon.ico"))?.Stream;
            image = stream is null ? (Icon)SystemIcons.Application.Clone() : new Icon(stream);
            return new(image, text, open, settings, updates, exit, operation ?? (() => { }), result ?? open);
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
        _result.Text = _text("Tray.ViewResult");
        if (_captureFolder is not null) _captureFolder.Text = _text("Capture.OpenFolder");
        if (_captureMonitor is not null) _captureMonitor.Text = _text("Capture.TrayScreenshot");
        if (_captureStop is not null) _captureStop.Text = _text("Capture.Stop");
        _operation.Visible = _operationSeparator.Visible = _phase.HasValue;
        _operation.Enabled = _phase != BackgroundOperationPhase.Pausing;
        _operation.Text = _countdown is int seconds ? string.Format(_text("Tray.Countdown"), TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss"))
            : _text(_phase switch { BackgroundOperationPhase.Pausing => "Tray.Pausing", BackgroundOperationPhase.Running => "Tray.Pause", _ => "Tray.Resume" });
    }

    public void SetOperation(BackgroundOperationPhase? phase, int? countdown = null)
    { _phase = phase; _countdown = countdown; RefreshLocalization(); }

    public void AddCaptureCommands(Action folder, Action screenshot, Action? stop = null)
    {
        if (_disposed || _captureFolder is not null) return;
        _captureFolder = new(); _captureMonitor = new();
        _captureFolder.Click += (_, _) => folder(); _captureMonitor.Click += (_, _) => screenshot();
        _menu.Items.Insert(3, _captureMonitor); _menu.Items.Insert(4, _captureFolder); RefreshLocalization();
        if (stop is not null)
        { _captureStop = new() { Visible = false, Text = _text("Capture.Stop") }; _captureStop.Click += (_, _) => stop(); _menu.Items.Insert(5, _captureStop); }
    }
    public void SetCaptureRecording(bool recording) { if (_captureStop is not null) _captureStop.Visible = recording; }

    public void SetAttention(bool result, bool update)
    {
        if (_disposed) return;
        var attention = result ? 2 : update ? 1 : 0;
        _result.Visible = result;
        if (_attention == attention) return;
        Icon? next = null;
        if (result || update)
        {
            using var bitmap = new Bitmap(32, 32);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.DrawIcon(_image, new Rectangle(0, 0, 32, 32));
                using var brush = new SolidBrush(result ? Color.FromArgb(255, 73, 82) : Color.FromArgb(42, 224, 137));
                graphics.FillEllipse(Brushes.Black, 18, 18, 14, 14); graphics.FillEllipse(brush, 20, 20, 10, 10);
            }
            var handle = bitmap.GetHicon();
            try { using var temporary = Icon.FromHandle(handle); next = (Icon)temporary.Clone(); }
            finally { DestroyIcon(handle); }
        }
        _icon.Icon = next ?? _image; _markedImage?.Dispose(); _markedImage = next;
        _attention = attention;
    }

    public bool Notify(string title, string message, bool warning = false, Action? onOpen = null)
    {
        if (!IsAvailable) return false;
        _balloonAction = onOpen;
        _icon.ShowBalloonTip(15000, title, message, warning ? ToolTipIcon.Warning : ToolTipIcon.Info); return true;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _icon.Visible = false; _icon.Dispose(); _menu.Dispose(); _markedImage?.Dispose(); _image.Dispose();
    }
}
