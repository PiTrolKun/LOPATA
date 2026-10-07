using System.IO;
using System.Windows;
using AIHub.Models;
using AIHub.Services;

namespace AIHub;

public partial class MainWindow
{
    private ComponentAcquisitionPlan? _hardwareRuntimePlan;
    private readonly SemaphoreSlim _hardwareRuntimeCheck = new(1, 1);

    private async Task<bool> CheckHardwareRuntimePromptAsync()
    {
        await _hardwareRuntimeCheck.WaitAsync();
        try
        {
            if (_processShutdownPending || _processShutdownComplete) return false;
            if (_coreModelDownloadCts is not null) return false;
            // Refresh the inventory: installed model weights and an old passport do not prove runtime readiness.
            var passport = await Task.Run(() => new ComputerPassportService().RegeneratePassport());
            _hardwareRuntimePlan = await new HardwareRuntimePreparation(_componentManager).CheckAsync(passport.Gpus, CancellationToken.None);
            if (_processShutdownPending || _processShutdownComplete) return false;
            if (_coreModelDownloadCts is not null) return false;
            if (_hardwareRuntimePlan.IsReady) return true;
            ShowHardwareRuntimePrompt();
            return false;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
            or System.Text.Json.JsonException or InvalidOperationException)
        {
            StatusText.Text = L("HardwareRuntime.CheckFailed");
            return false;
        }
        finally { _hardwareRuntimeCheck.Release(); }
    }

    private void ShowHardwareRuntimePrompt()
    {
        _pendingModelDownload = PendingModelDownload.Hardware;
        CoreModelPromptPanel.Visibility = Visibility.Visible;
        CoreModelDownloadPanel.Visibility = Visibility.Collapsed;
        CoreModelPromptText.Text = LF("HardwareRuntime.Prompt",
            string.Join(", ", _hardwareRuntimePlan!.Items.Where(item => !item.AlreadyAvailable).Select(item => item.Name)),
            FormatBytes(_hardwareRuntimePlan.TotalDownloadBytes));
        DownloadCoreModelButton.IsEnabled = true;
        StatusText.Text = L("HardwareRuntime.Missing");
    }

    private async Task StartHardwareRuntimeDownloadAsync()
    {
        if (_coreModelDownloadCts is not null || _hardwareRuntimePlan is null) return;
        var plan = _hardwareRuntimePlan;
        _coreModelDownloadCts = new CancellationTokenSource();
        DownloadCoreModelButton.IsEnabled = false;
        CoreModelPromptPanel.Visibility = Visibility.Collapsed;
        CoreModelDownloadPanel.Visibility = Visibility.Visible;
        CoreModelDownloadProgressBar.Value = 0;
        CoreModelDownloadTitleText.Text = L("HardwareRuntime.Preparing");
        var progress = new Progress<ComponentDownloadProgress>(value =>
        {
            CoreModelDownloadProgressBar.IsIndeterminate = value.TotalBytes <= 0;
            var percent = value.TotalBytes <= 0 ? 0 : Math.Clamp(100d * value.DownloadedBytes / value.TotalBytes, 0, 100);
            CoreModelDownloadProgressBar.Value = percent;
            CoreModelDownloadTitleText.Text = LF("HardwareRuntime.Progress", ComponentCatalog.Find(value.ComponentId)?.Name ?? value.ComponentId,
                percent, FormatBytes(value.DownloadedBytes), FormatBytes(value.TotalBytes));
        });
        try
        {
            await new HardwareRuntimePreparation(_componentManager).InstallAsync(plan, progress, _coreModelDownloadCts.Token);
            HideCoreModelPrompt();
            StatusText.Text = L("HardwareRuntime.Ready");
        }
        catch (OperationCanceledException) { ShowHardwareRuntimePrompt(); StatusText.Text = L("HardwareRuntime.Paused"); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
            or System.Text.Json.JsonException or InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException or System.Net.Http.HttpRequestException)
        { ShowHardwareRuntimePrompt(); StatusText.Text = L("HardwareRuntime.DownloadFailed"); }
        finally
        {
            CoreModelDownloadPanel.Visibility = Visibility.Collapsed;
            CoreModelDownloadProgressBar.IsIndeterminate = false;
            _coreModelDownloadCts.Dispose(); _coreModelDownloadCts = null;
            DownloadCoreModelButton.IsEnabled = true;
            RefreshComponentCatalogUi();
        }
    }
}
