using System.Windows;
using System.Windows.Threading;
using AIHub.Services;
using AIHub.Services.VideoEditor;

namespace AIHub;

public partial class MainWindow
{
    private int _preparationBusyCount;

    private void BeginPreparationBusy()
    {
        _preparationBusyCount++;
        StartupBusyOverlay.Message = L("Startup.Checking");
        StartupBusyOverlay.Visibility = Visibility.Visible;
    }

    private void EndPreparationBusy()
    {
        if (--_preparationBusyCount == 0)
            StartupBusyOverlay.Visibility = Visibility.Collapsed;
    }

    private async Task InitializeStartupChecksAsync()
    {
        BeginPreparationBusy();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        OwnedProcessRegistry.Log("startup_check_started", "StartupPreparation");
        try
        {
            // Render a first frame before checks; hidden tray launches stay hidden.
            await Dispatcher.Yield(DispatcherPriority.Background);
            OwnedProcessRegistry.Log("startup_check_started", "ComputerPassport");
            var fullAudit = ComponentAudit.Enabled;
            var passport = await Task.Run(() => _computerPassportService.ForStartup(fullAudit), _backgroundLifetime.Token);
            OwnedProcessRegistry.Log("startup_check_finished", "ComputerPassport", detail: $"elapsedMs={elapsed.ElapsedMilliseconds}");
            if (_backgroundLifetime.IsCancellationRequested || _processShutdownPending || _processShutdownComplete) return;
            _lastPassport = passport;
            SavePassportState(passport);
            UpdateComputerPassportStep(passport);
            UpdateWelcomeStatus();
            if (!IsVisible || await EnsureIslandMusicVoiceAsync())
                await EvaluateCoreModelOnStartupAsync();
        }
        catch (OperationCanceledException) when (_backgroundLifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            OwnedProcessRegistry.Log("startup_check_failed", "StartupPreparation", detail: error.GetType().Name);
            StatusText.Text = L("Status.PassportMissing");
        }
        finally
        {
            OwnedProcessRegistry.Log("startup_check_finished", "StartupPreparation", detail: $"elapsedMs={elapsed.ElapsedMilliseconds}");
            EndPreparationBusy();
        }
    }

    private Task<bool>? _islandVoicePreparation;
    private bool _islandVoicePreparing;

    private async Task<bool> EnsureIslandMusicVoiceAsync()
    {
        if (App.SafeCrashRecoveryLaunch || !IslandRuntime.IsAvailable) return true;
        var task = _islandVoicePreparation ??= PrepareIslandMusicVoiceAsync();
        try { return await task; }
        finally
        {
            if (ReferenceEquals(_islandVoicePreparation, task)) _islandVoicePreparation = null;
        }
    }

    private async Task<bool> PrepareIslandMusicVoiceAsync()
    {
        var token = _backgroundLifetime.Token;
        var model = Services.Dictation.DictationComponents.ModelId;
        var library = new ManagedModelLibraryStore();
        BeginPreparationBusy();
        try
        {
            var kittenPrepared = IslandKittenVoice.IsPrepared(library);
            var cpuPrepared = ManagedPythonRuntime.HasCpu;
            if (kittenPrepared && cpuPrepared && Services.Dictation.DictationComponents.IsInstalled(_componentManager)
                && Services.Dictation.DictationComponents.HasAcknowledgement) return true;

            _islandVoicePreparing = true;
            StartupBusyOverlay.Message = L("Island.VoicePreparing");
            await Dispatcher.Yield(DispatcherPriority.Background);
            await ComponentLicenseGate.EnsureAsync(Services.Dictation.DictationComponents.TermsId, token);
            void ReportProgress(long downloaded, long total)
            {
                if (!_islandVoicePreparing) return;
                StartupBusyOverlay.Message = total > 0
                    ? LF("Island.VoicePrepareProgress", Math.Clamp(100d * downloaded / total, 0, 100))
                    : L("Island.VoicePreparing");
            }
            var progress = new Progress<Models.ComponentDownloadProgress>(update =>
                ReportProgress(update.DownloadedBytes, update.TotalBytes));
            if (!Services.Dictation.DictationComponents.IsInstalled(_componentManager))
                await _componentManager.DownloadAndInstallAsync(model, progress, token);
            if (!Services.Dictation.DictationComponents.IsInstalled(_componentManager))
                throw new System.IO.InvalidDataException("The Island voice model is incomplete.");

            if (!cpuPrepared)
                await _componentManager.DownloadAndInstallAsync(HardwareRuntimeCatalog.PythonCpuId, progress, token);
            if (!ManagedPythonRuntime.HasCpu)
                throw new System.IO.InvalidDataException("The Island voice Python runtime is incomplete.");

            if (!kittenPrepared)
            {
                var voice = VideoNarratorCatalog.KittenMini;
                var installedRoot = library.Load(VideoNarratorCatalog.ArtifactId(voice))?.ModelsRoot;
                var modelsRoot = new[] { installedRoot }.Concat(_storageSettings.Models.Locations.Select(location => location.Path))
                    .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path)) ?? AppDataPaths.ComponentModelsDirectory;
                using var preparation = new VideoModelPreparation(library)
                {
                    ParallelConnections = _appSettings.ModelDownloads?.MaximumParallelConnections ?? 0
                };
                var voiceProgress = new Progress<Models.ManagedModelDownloadProgress>(update =>
                    ReportProgress(update.DownloadedBytes, update.TotalBytes));
                if (!await preparation.PrepareAsync(modelsRoot, new("speech", [voice]), download: true, voiceProgress, token))
                    throw new System.IO.InvalidDataException("Kitten TTS 2 Mini is incomplete.");
            }
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
        catch (Exception error)
        {
            OwnedProcessRegistry.Log("island_voice_preparation_failed", "StartupPreparation", detail: error.GetType().Name);
            StatusText.Text = L("Island.VoicePrepareFailed");
            return false;
        }
        finally { _islandVoicePreparing = false; EndPreparationBusy(); }
    }
}
