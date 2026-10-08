using System.IO;
using System.Windows;
using AIHub.Models;
using AIHub.Services;

namespace AIHub;

public partial class MainWindow
{
    private ComponentAcquisitionPlan? _hardwareRuntimePlan;
    private readonly SemaphoreSlim _hardwareRuntimeCheck = new(1, 1);
    private (long Length, DateTime Written)? _readyHardwareState;

    private (long Length, DateTime Written) ReadHardwareStateStamp()
    {
        var file = new FileInfo(AppDataPaths.ComponentStatePath);
        return file.Exists ? (file.Length, file.LastWriteTimeUtc) : (-1, DateTime.MinValue);
    }

    private bool IsHardwareRuntimeCheckCurrent()
    {
        if (_hardwareRuntimePlan?.IsReady != true || _readyHardwareState is null) return false;
        try
        {
            if (_readyHardwareState != ReadHardwareStateStamp()) return false;
            // A same-session entry needs only the lightweight layout check.
            // Installation/removal or a missing artifact requires a new acquisition plan.
            // Content verification belongs to installation and selected-runtime execution.
            return _componentManager.BuildPlanForComponents(
                _hardwareRuntimePlan.Items.Select(item => item.ComponentId), "Hardware runtime re-entry").IsReady;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or System.Text.Json.JsonException or InvalidOperationException) { return false; }
    }

    private async Task<bool> CheckHardwareRuntimePromptAsync(ComputerPassport? knownPassport = null)
    {
        await _hardwareRuntimeCheck.WaitAsync();
        BeginPreparationBusy();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        OwnedProcessRegistry.Log("startup_check_started", "HardwareReadiness");
        try
        {
            if (_processShutdownPending || _processShutdownComplete) return false;
            if (_coreModelDownloadCts is not null) return false;
            // Reuse only the inventory just collected by startup; later checks refresh it.
            _hardwareRuntimePlan = await Task.Run(async () =>
            {
                var passport = knownPassport ?? new ComputerPassportService().RegeneratePassport();
                return await new HardwareRuntimePreparation(_componentManager).CheckReadinessAsync(passport.Gpus, _backgroundLifetime.Token);
            }, _backgroundLifetime.Token);
            if (_processShutdownPending || _processShutdownComplete) return false;
            if (_coreModelDownloadCts is not null) return false;
            if (_hardwareRuntimePlan.IsReady)
            {
                _readyHardwareState = ReadHardwareStateStamp();
                return true;
            }
            ShowHardwareRuntimePrompt();
            return false;
        }
        catch (OperationCanceledException) when (_backgroundLifetime.IsCancellationRequested) { return false; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
            or System.Text.Json.JsonException or InvalidOperationException)
        {
            OwnedProcessRegistry.Log("startup_check_failed", "HardwareReadiness", detail: error.GetType().Name);
            StatusText.Text = L("HardwareRuntime.CheckFailed");
            return false;
        }
        finally
        {
            OwnedProcessRegistry.Log("startup_check_finished", "HardwareReadiness", detail: $"elapsedMs={elapsed.ElapsedMilliseconds}");
            EndPreparationBusy(); _hardwareRuntimeCheck.Release();
        }
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
