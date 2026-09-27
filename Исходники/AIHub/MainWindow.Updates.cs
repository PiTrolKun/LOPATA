using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using AIHub.Models;
using AIHub.Services;
using Lopata.Updates;

namespace AIHub;

public partial class MainWindow
{
    private readonly HttpClient _updateHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly CancellationTokenSource _updateLifetime = new();
    private ApplicationUpdateCoordinator? _updateService;
    private ApplicationUpdateWindow? _updateWindow;
    private UpdateOffer? _availableUpdate;
    private ProcessStartInfo? _applicationUpdateStart;
    private readonly DispatcherTimer _betaUpdateTimer = new() { Interval = TimeSpan.FromMinutes(30) };
    private bool _automaticUpdateCheckRunning, _checkAfterUpdateWindow;
    private string? _lastOfferedUpdateVersion;

    private void InitializeApplicationUpdates()
    {
        _appSettings.Updates ??= new();
        _betaUpdateTimer.Tick += async (_, _) =>
        {
            _betaUpdateTimer.Stop();
            await CheckAutomaticUpdateAsync();
        };
        Closed += (_, _) => _betaUpdateTimer.Stop();
        Closed += (_, _) =>
        {
            if (_applicationUpdateStart is not { } start) return;
            try { _ = Process.Start(start) ?? throw new IOException("Updater did not start."); }
            catch (Exception) { System.Windows.MessageBox.Show(L("Updates.InstallFailed"), L("Updates.Title")); }
        };
        try { _updateService = new(_updateHttp, Path.Combine(AppDataPaths.BaseDirectory, "Updates")); }
        catch (Exception) { /* Manual opening explains damaged update state; startup remains available. */ }
        ContentRendered += async (_, _) =>
        {
            try { await ApplicationUpdateStartup.ConfirmHealthyAsync(_updateLifetime.Token); }
            catch (Exception)
            {
                if (!_updateLifetime.IsCancellationRequested)
                    System.Windows.MessageBox.Show(this, L("Updates.HealthFailed"), L("Updates.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            await CheckAutomaticUpdateAsync();
        };
    }

    private void ScheduleBetaUpdateCheck()
    {
        _betaUpdateTimer.Stop();
        if (!_updateLifetime.IsCancellationRequested && _updateService is not null
            && _appSettings.Updates.CheckOnStartup && _updateService.ReadDirection() == UpdateDelivery.FilePatch)
            _betaUpdateTimer.Start();
    }

    private async Task CheckAutomaticUpdateAsync()
    {
        if (_updateService is null || _updateLifetime.IsCancellationRequested || _automaticUpdateCheckRunning) return;
        if (_updateWindow is not null) { ScheduleBetaUpdateCheck(); return; }
        _automaticUpdateCheckRunning = true;
        var checkSucceeded = false;
        UpdateOffer? checkedOffer = null;
        try
        {
            if (_updateService.ReadPrepared() is not null)
            {
                ApplicationUpdateNotice.Visibility = Visibility.Visible;
                return;
            }
            if (!_appSettings.Updates.CheckOnStartup || _updateService.ReadDirection() is not { } direction) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_updateLifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            var offer = await _updateService.CheckAsync(GetAppVersion(), direction, timeout.Token);
            if (_updateLifetime.IsCancellationRequested) return;
            checkSucceeded = true;
            checkedOffer = offer;
            _availableUpdate = offer;
            _appSettings.Updates.LastCheckUtc = offer is null ? DateTimeOffset.UtcNow : null;
            _appSettingsStore.Save(_appSettings);
            if (offer is null)
            {
                ApplicationUpdateNotice.Visibility = Visibility.Collapsed;
                return;
            }
            if (offer is not null && _lastOfferedUpdateVersion != offer.Version)
            {
                _lastOfferedUpdateVersion = offer.Version;
                ApplicationUpdateNotice.Visibility = Visibility.Visible;
            }
        }
        catch (Exception) { /* Background checks are silent offline; manual checks explain errors. */ }
        finally
        {
            _automaticUpdateCheckRunning = false;
            if (!_updateLifetime.IsCancellationRequested)
                _updateWindow?.CompleteBackgroundCheck(checkedOffer, checkSucceeded);
            ScheduleBetaUpdateCheck();
        }
    }

    private void UpdateSettingsChanged()
    {
        _appSettingsStore.Save(_appSettings);
        _betaUpdateTimer.Stop();
        _checkAfterUpdateWindow = _appSettings.Updates.CheckOnStartup
            && _updateService?.ReadDirection() == UpdateDelivery.FilePatch;
    }

    private void ApplicationUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_updateService is null)
        {
            System.Windows.MessageBox.Show(this, L("Updates.StateFailed"), L("Updates.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_updateWindow is not null) { _updateWindow.Activate(); return; }
        ApplicationUpdateNotice.Visibility = Visibility.Collapsed;
        try
        {
            _updateWindow = new(this, _updateService, _appSettings.Updates, GetAppVersion(), L,
                () => _appSettingsStore.Save(_appSettings), () => _appSettings.ModelDownloads.MaximumParallelConnections,
                InstallApplicationUpdateAsync, _availableUpdate, UpdateSettingsChanged,
                () => _checkAfterUpdateWindow = false);
        }
        catch (Exception)
        {
            _updateWindow = null;
            System.Windows.MessageBox.Show(this, L("Updates.StateFailed"), L("Updates.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _updateWindow.SetBackgroundCheckBusy(_automaticUpdateCheckRunning);
        _updateWindow.Closed += async (_, _) =>
        {
            var wasBusy = _updateWindow?.IsBusy == true;
            _updateWindow = null; _availableUpdate = null;
            if (_checkAfterUpdateWindow && !wasBusy)
            {
                _checkAfterUpdateWindow = false;
                await CheckAutomaticUpdateAsync();
            }
            else { _checkAfterUpdateWindow = false; ScheduleBetaUpdateCheck(); }
        };
        _updateWindow.Show();
    }

    private async Task InstallApplicationUpdateAsync()
    {
        var pending = _updateService?.ReadPrepared() ?? throw new InvalidOperationException("No prepared update.");
        ProcessStartInfo start;
        if (_updateService!.Installation is not null)
        {
            start = ApplicationUpdateStartup.Launcher("--apply");
            ApplicationUpdateStartup.AddWaitForThisProcess(start);
        }
        else
        {
            var update = ApplicationUpdateCoordinator.ToInstallerUpdate(ApplicationUpdateCoordinator.PreparedOffer(pending));
            if (!await ApplicationUpdateService.VerifyAsync(pending.InstallerPath!, update, _updateLifetime.Token))
                throw new InvalidDataException("Installer changed after verification.");
            start = new ProcessStartInfo(pending.InstallerPath!) { UseShellExecute = true };
            start.ArgumentList.Add($"/WAITFORPID={Environment.ProcessId}");
        }
        // A canceled editor/model shutdown must not apply an update. Launch only after normal Closing gates succeed.
        _applicationUpdateStart = start;
        _updateWindow?.Close();
        Close();
    }
}
