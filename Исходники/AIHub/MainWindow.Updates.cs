using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using AIHub.Models;
using AIHub.Services;

namespace AIHub;

public partial class MainWindow
{
    private readonly HttpClient _updateHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly CancellationTokenSource _updateLifetime = new();
    private ApplicationUpdateService? _updateService;
    private ApplicationUpdateWindow? _updateWindow;
    private ApplicationUpdate? _availableUpdate;

    private void InitializeApplicationUpdates()
    {
        _appSettings.Updates ??= new();
        _updateService = new(_updateHttp, Path.Combine(AppDataPaths.BaseDirectory, "Updates"));
        ContentRendered += async (_, _) =>
        {
            if (!_appSettings.Updates.CheckOnStartup) return;
            var last = _appSettings.Updates.LastCheckUtc;
            if (last is not null && DateTimeOffset.UtcNow - last >= TimeSpan.Zero
                && DateTimeOffset.UtcNow - last < TimeSpan.FromHours(6)) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_updateLifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            try
            {
                _availableUpdate = await _updateService.CheckAsync(GetAppVersion(),
                    ApplicationUpdateWindow.IncludeBeta(_appSettings.Updates, GetAppVersion()), timeout.Token);
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
        if (_updateService is null) return;
        if (_updateWindow is not null) { _updateWindow.Activate(); return; }
        ApplicationUpdateNotice.Visibility = Visibility.Collapsed;
        _updateWindow = new(this, _updateService, _appSettings.Updates, GetAppVersion(), L,
            () => _appSettingsStore.Save(_appSettings), () => _appSettings.ModelDownloads.MaximumParallelConnections,
            InstallApplicationUpdateAsync, _availableUpdate);
        _updateWindow.Closed += (_, _) => { _updateWindow = null; _availableUpdate = null; };
        _updateWindow.Show();
    }

    private async Task InstallApplicationUpdateAsync(ApplicationUpdate update, string path)
    {
        if (System.Windows.MessageBox.Show(_updateWindow, L("Updates.InstallConfirm"), L("Updates.Title"),
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        if (!await ApplicationUpdateService.VerifyAsync(path, update, _updateLifetime.Token))
            throw new InvalidDataException("Installer changed after verification.");
        var start = new ProcessStartInfo(path) { UseShellExecute = true };
        start.ArgumentList.Add($"/WAITFORPID={Environment.ProcessId}");
        // Installed copies upgrade their current directory. Dev copies use the installer's default directory.
        if (AppDataPaths.ProjectRoot is null) start.ArgumentList.Add($"/DIR={AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)}");
        _ = Process.Start(start) ?? throw new IOException("Installer did not start.");
        System.Windows.Application.Current.Shutdown();
    }
}
