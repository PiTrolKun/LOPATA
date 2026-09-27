using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
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

    private void InitializeApplicationUpdates()
    {
        _appSettings.Updates ??= new();
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
            if (_updateService is null) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_updateLifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            try
            {
                if (_updateService.ReadPrepared() is not null) { ApplicationUpdateNotice.Visibility = Visibility.Visible; return; }
                if (!_appSettings.Updates.CheckOnStartup || _updateService.ReadDirection() is not { } direction) return;
                _availableUpdate = await _updateService.CheckAsync(GetAppVersion(),
                    direction, timeout.Token);
                if (_updateLifetime.IsCancellationRequested) return;
                _appSettings.Updates.LastCheckUtc = _availableUpdate is null ? DateTimeOffset.UtcNow : null;
                _appSettingsStore.Save(_appSettings);
                ApplicationUpdateNotice.Visibility = _availableUpdate is null ? Visibility.Collapsed : Visibility.Visible;
            }
            catch (Exception) { /* Startup stays usable offline; manual checks explain errors. */ }
        };
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
                InstallApplicationUpdateAsync, _availableUpdate);
        }
        catch (Exception)
        {
            _updateWindow = null;
            System.Windows.MessageBox.Show(this, L("Updates.StateFailed"), L("Updates.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _updateWindow.Closed += (_, _) => { _updateWindow = null; _availableUpdate = null; };
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
