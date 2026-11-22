using System.ComponentModel;
using System.Diagnostics;
using Lopata.Inspector;

namespace Lopata.PcInspector;

internal sealed class InspectorTray : ApplicationContext
{
    private readonly InspectorStore _store;
    private readonly string _applicationPath;
    private readonly SynchronizationContext _ui;
    private readonly NotifyIcon _tray;
    private readonly Icon _icon;
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _report;
    private readonly ToolStripMenuItem _stop;
    private readonly ToolStripMenuItem _fullAudit;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly DateTimeOffset _startedUtc;
    private Task _observer;
    private bool _stopping;
    private bool _disposed;

    internal InspectorTray(InspectorStore store, string applicationPath, DateTimeOffset startedUtc)
    {
        _store = store;
        _applicationPath = applicationPath;
        _startedUtc = startedUtc;
        _ui = SynchronizationContext.Current ?? throw new InvalidOperationException();
        _icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? (Icon)SystemIcons.Information.Clone();
        _report = new ToolStripMenuItem();
        _stop = new ToolStripMenuItem();
        _fullAudit = new ToolStripMenuItem { Enabled = false };
        _menu.Items.AddRange([_report, _stop, _fullAudit]);
        _menu.Opening += (_, _) => RefreshMenu();
        _report.Click += (_, _) => OpenReport();
        _stop.Click += async (_, _) => await StopManuallyAsync();
        _tray = new NotifyIcon { Text = "LOPATA Controller PC", Icon = _icon, ContextMenuStrip = _menu, Visible = true };
        _tray.DoubleClick += (_, _) => OpenReport();
        _observer = StartObserving();
        _timer = new System.Windows.Forms.Timer { Interval = 500 };
        _timer.Tick += (_, _) =>
        {
            if (!_stopping && _observer.IsCompleted) ReviewFinishedObserver();
        };
        _timer.Start();
    }

    internal int ExitCode { get; private set; }

    private Task StartObserving() => Task.Run(() => new InspectorObserver(_store, ShowNotification)
        .RunAsync(_startedUtc, _shutdown.Token));

    private void ReviewFinishedObserver()
    {
        if (_observer.Exception?.GetBaseException() is { } error)
        {
            ExitCode = 1;
            RecordFailure(_store, error);
        }
        else if (!_shutdown.IsCancellationRequested && _store.ReadState().Enabled && !_store.StopRequested(_startedUtc))
        {
            // Full audit can be enabled while a quick-mode stop is still completing.
            _observer = StartObserving();
            return;
        }
        ExitThread();
    }

    private void RefreshMenu()
    {
        var russian = _store.ReadRussian();
        var state = _store.ReadState();
        _report.Text = russian ? "Запросить отчёт через Ядро" : "Request a report from the Core";
        _stop.Text = russian ? "Выключить инспектор" : "Turn off the inspector";
        _stop.Enabled = !_stopping && !state.FullAudit;
        _report.Enabled = !_stopping;
        _fullAudit.Text = russian ? "Инспектор работает с полным аудитом компонентов" : "The inspector is required by full component audit";
        _fullAudit.Visible = state.FullAudit;
    }

    private async Task StopManuallyAsync()
    {
        if (_stopping) return;
        _stopping = true;
        try
        {
            if (_store.SetEnabled(false).Enabled) return;
            await Task.Run(() => InspectorStartup.DisableAutomaticStart(_store.ProfileRoot, _applicationPath));
            if (!_store.ReadState().Enabled) ExitThread();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
                                     or Win32Exception or System.Runtime.InteropServices.COMException)
        {
            var russian = _store.ReadRussian();
            _tray.ShowBalloonTip(4000, "LOPATA Controller PC",
                russian ? "Не удалось полностью изменить состояние инспектора." : "The inspector setting could not be applied completely.",
                ToolTipIcon.Warning);
        }
        finally { _stopping = false; }
    }

    private void OpenReport()
    {
        if (_stopping) return;
        try
        {
            var start = new ProcessStartInfo(_applicationPath)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(_applicationPath)!
            };
            start.ArgumentList.Add("--inspector-report");
            start.Environment["LOPATA_UPDATE_STAND_ROOT"] = _store.ProfileRoot;
            using var process = Process.Start(start);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            var russian = _store.ReadRussian();
            _tray.ShowBalloonTip(4000, "LOPATA Controller PC", russian ? "ЛОПАТА недоступна для отчёта." :
                "LOPATA is unavailable for this report.", ToolTipIcon.Warning);
        }
    }

    private bool ShowNotification(string title, string body)
    {
        if (_shutdown.IsCancellationRequested) return false;
        if (InspectorNotifications.TryShow(title, body, _store.ReadRussian())) return true;
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(_ =>
        {
            if (_shutdown.IsCancellationRequested) { pending.TrySetResult(false); return; }
            try
            {
                _tray.ShowBalloonTip(5000, title, body, ToolTipIcon.Info);
                pending.TrySetResult(true);
            }
            catch (InvalidOperationException) { pending.TrySetResult(false); }
        }, null);
        return pending.Task.Wait(TimeSpan.FromMilliseconds(700)) && pending.Task.Result;
    }

    protected override void ExitThreadCore()
    {
        _shutdown.Cancel();
        _tray.Visible = false;
        _timer.Stop();
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _shutdown.Cancel();
            try { _observer.Wait(TimeSpan.FromSeconds(2)); }
            catch (AggregateException) { }
            _timer.Dispose();
            _tray.Dispose();
            _menu.Dispose();
            _icon.Dispose();
            _shutdown.Dispose();
        }
        base.Dispose(disposing);
    }

    internal static void RecordFailure(InspectorStore store, Exception error) =>
        store.Record(new InspectorEvent
        {
            OperationId = "inspector",
            Kind = InspectorEventKind.OperationFinished,
            Outcome = InspectorOutcome.Failure,
            Detail = error.GetType().Name + ":0x" + error.HResult.ToString("x8", System.Globalization.CultureInfo.InvariantCulture)
        });
}
