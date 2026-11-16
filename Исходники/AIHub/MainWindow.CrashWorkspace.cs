using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Services;
using Forms = System.Windows.Forms;

namespace AIHub;

public partial class MainWindow
{
    private CrashWorkspaceSnapshot? _crashWorkspaceRestore;

    internal bool? CrashWorkspaceStartsInTray => _crashWorkspaceRestore?.WasInTray;

    internal void InitializeCrashWorkspaceRecovery(bool restore)
    {
        var store = CrashWorkspaceStore.ForCurrentInstallation();
        if (restore || App.SafeCrashRecoveryLaunch)
        {
            try { _crashWorkspaceRestore = store.Load(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            { LogCrashWorkspaceFailure("workspace_read_failed", error); }
        }

        byte[]? previous = null;
        var writing = false;
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        { Interval = TimeSpan.FromMilliseconds(750) };
        async void Capture(object? _, EventArgs __)
        {
            if (writing || _processShutdownPending || _processShutdownComplete || Dispatcher.HasShutdownStarted) return;
            try
            {
                if (CaptureCrashWindowLayout(this) is not { } layout) return;
                var windows = System.Windows.Application.Current.Windows.OfType<Window>()
                    .Where(window => window != this && window.IsVisible)
                    .Select(CaptureCrashWorkspaceWindow).OfType<CrashWorkspaceWindow>().Take(32).ToArray();
                var page = CaptureCrashWorkspacePage();
                if (App.SafeCrashRecoveryLaunch && previous is null && page.Id == nameof(WelcomePage)
                    && !page.SettingsOpen && windows.Length == 0) return;
                var snapshot = new CrashWorkspaceSnapshot(CrashWorkspaceStore.CurrentSchema, page, layout,
                    !IsVisible && CanStartInTray, windows);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot);
                if (previous is not null && previous.AsSpan().SequenceEqual(bytes)) return;
                writing = true;
                await Task.Run(() => store.Save(bytes));
                previous = bytes;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            { LogCrashWorkspaceFailure("workspace_save_failed", error); }
            finally { writing = false; }
        }

        timer.Tick += Capture;
        Closed += (_, _) => timer.Stop();
        SourceInitialized += (_, _) =>
        {
            var snapshot = _crashWorkspaceRestore;
            if (snapshot is not null)
            {
                try
                {
                    var layout = snapshot.MainWindow;
                    if (App.SafeCrashRecoveryLaunch && layout.WindowState == (int)WindowState.Minimized)
                        layout = layout with { WindowState = (int)WindowState.Normal };
                    ApplyCrashWindowLayout(this, layout);
                }
                catch (Exception error) { LogCrashWorkspaceFailure("workspace_geometry_restore_failed", error); }
            }
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(async () =>
            {
                if (restore && snapshot is not null)
                {
                    try { await RestoreCrashWorkspaceAsync(snapshot); }
                    catch (OperationCanceledException) when (_backgroundLifetime.IsCancellationRequested) { return; }
                    catch (Exception error) { LogCrashWorkspaceFailure("workspace_restore_failed", error); }
                }
                if (!Dispatcher.HasShutdownStarted && !_backgroundLifetime.IsCancellationRequested)
                {
                    Capture(this, EventArgs.Empty);
                    timer.Start();
                }
            }));
        };
    }

    private async Task RestoreCrashWorkspaceAsync(CrashWorkspaceSnapshot snapshot)
    {
        try { await RestoreCrashWorkspacePageAsync(snapshot.Page); }
        catch (Exception error) { LogCrashWorkspaceFailure("workspace_page_restore_failed", error); }

        foreach (var entry in snapshot.Windows)
        {
            if (_backgroundLifetime.IsCancellationRequested) return;
            try { await RestoreCrashWorkspaceWindowAsync(entry); }
            catch (OperationCanceledException) when (_backgroundLifetime.IsCancellationRequested) { return; }
            catch (Exception error) { LogCrashWorkspaceFailure("workspace_window_restore_failed", error); }
        }
    }

    private CrashWindowLayout? CaptureCrashWindowLayout(Window window)
    {
        var source = PresentationSource.FromVisual(window);
        if (source?.CompositionTarget is not { } composition) return null;
        var bounds = window.RestoreBounds;
        if (bounds.IsEmpty || !double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y)
            || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height)) return null;
        bounds.Transform(composition.TransformToDevice);
        var center = new System.Drawing.Point((int)(bounds.Left + bounds.Width / 2), (int)(bounds.Top + bounds.Height / 2));
        var screen = Forms.Screen.FromPoint(center);
        var wasMaximized = window.WindowState == WindowState.Maximized
            || (window == this && window.WindowState == WindowState.Minimized && _lastNonMinimizedWindowState == WindowState.Maximized);
        return new(_interfaceLayoutService.CapturePlacement(bounds, ToRect(screen.WorkingArea), screen.DeviceName,
            wasMaximized), (int)window.WindowState);
    }

    private void ApplyCrashWindowLayout(Window window, CrashWindowLayout layout)
    {
        if (!layout.Placement.HasValue) return;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var workArea = ToRect(FindRememberedScreen(layout.Placement.MonitorDeviceName, handle).WorkingArea);
        var dpi = VisualTreeHelper.GetDpi(window);
        var bounds = _interfaceLayoutService.CalculateRememberedBounds(layout.Placement, workArea,
            window.MinWidth * dpi.DpiScaleX, window.MinHeight * dpi.DpiScaleY);
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.WindowState = WindowState.Normal;
        SetWindowPos(handle, IntPtr.Zero, (int)Math.Round(bounds.Left), (int)Math.Round(bounds.Top),
            (int)Math.Round(bounds.Width), (int)Math.Round(bounds.Height), SwpNoZOrder | SwpNoActivate);
        if (layout.Placement.WasMaximized) window.WindowState = WindowState.Maximized;
        if (layout.WindowState == (int)WindowState.Minimized) window.WindowState = WindowState.Minimized;
    }

    private static void LogCrashWorkspaceFailure(string action, Exception error) =>
        OwnedProcessRegistry.Log(action, "Application", detail: error.GetType().Name);
}
