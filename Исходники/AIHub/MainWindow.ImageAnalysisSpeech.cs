using System.Text.Json;
using System.Windows;
using System.IO;
using System.Media;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using WpfMessageBox = System.Windows.MessageBox;

namespace AIHub;

public partial class MainWindow
{
    private readonly CoreSpeechPresentationCoordinator _imageAnalysisProgrammaticSpeechCoordinator =
        new(new CoreVoiceEngineRouter(new EspeakCoreVoiceEngine(), new RhVoiceCoreVoiceEngine()));
    private KokoroSpeechRuntimeService? _imageAnalysisKokoroSpeechService;
    private CancellationTokenSource? _imageAnalysisSpeechCts;
    private CancellationTokenSource? _imageAnalysisSpeechWarmupCts;
    private CancellationTokenSource? _imageAnalysisVoiceDownloadCts;
    private string _lastAutoSpokenImageAnalysisFingerprint = string.Empty;
    private SoundPlayer? _imageAnalysisOmniPlayer;

    private bool IsHeavyImageAnalysis =>
        ImageAnalysisModeCapabilities.UsesOmniConversation(_imageAnalysisLiterarySession?.BundleId)
        || ImageAnalysisModeCapabilities.UsesOmniConversation(_selectedImageAnalysisBundle?.Id);

    private string HeavySpeechProfileKey
    {
        get
        {
            var bundle = _imageAnalysisLiterarySession?.BundleId ?? _selectedImageAnalysisBundle?.Id;
            var profile = OmniLlamaProfile.ForBundle(bundle);
            return profile is not null
                ? $"{profile.BundleId}|{profile.Repository}|{profile.Revision}"
                : $"{ImageAnalysisBundleCatalog.HeavyId}|{ManagedModelCatalog.Qwen25OmniRepository}|{ManagedModelCatalog.Qwen25OmniRevision}";
        }
    }

    private ImageAnalysisHeavySpeechSettings GetHeavyImageAnalysisSpeechSettings()
    {
        _appSettings.ImageAnalysisHeavySpeechProfiles ??= [];
        if (!_appSettings.ImageAnalysisHeavySpeechProfiles.TryGetValue(
                HeavySpeechProfileKey,
                out var settings))
        {
            settings = new ImageAnalysisHeavySpeechSettings();
            _appSettings.ImageAnalysisHeavySpeechProfiles[HeavySpeechProfileKey] = settings;
        }
        return settings;
    }

    private KokoroSpeechRuntimeService GetImageAnalysisKokoroSpeechService() =>
        _imageAnalysisKokoroSpeechService ??= new KokoroSpeechRuntimeService(
            _imageAnalysisBundleInstallationService.LibraryStore);

    private void RefreshImageAnalysisSpeechUi(string? status = null, bool isBusy = false)
    {
        if (IsHeavyImageAnalysis)
        {
            var heavySettings = GetHeavyImageAnalysisSpeechSettings();
            var kokoro = GetImageAnalysisKokoroSpeechService();
            var installedHeavyKokoro = kokoro.IsModelInstalled(_appSettings.LanguageCode);
            var canReplayHeavy = ImageAnalysisSpeechTextService
                .BuildSegments(_imageAnalysisLiterarySession?.ReviewSummary)
                .Count > 0;
            status ??= heavySettings.Mode switch
            {
                // Omni readiness statuses are retired with the II+ choice.
                ImageAnalysisSpeechModes.Kokoro when !installedHeavyKokoro =>
                    L("ImageAnalysis.Workspace.Voice.ModelMissing"),
                ImageAnalysisSpeechModes.Kokoro when kokoro.IsWarm(_appSettings.LanguageCode) =>
                    L("ImageAnalysis.Workspace.Voice.KokoroReady"),
                ImageAnalysisSpeechModes.Programmatic =>
                    L("ImageAnalysis.Workspace.Voice.ProgrammaticReady"),
                _ => string.Empty
            };
            ImageAnalysisWorkspacePage.SetHeavySpeechState(
                heavySettings,
                installedHeavyKokoro,
                canReplayHeavy,
                status,
                isBusy);
            RefreshSessionAudioUi();
            return;
        }
        _appSettings.ImageAnalysisSpeech ??= new ImageAnalysisSpeechSettings();
        var settings = _appSettings.ImageAnalysisSpeech;
        var mode = settings.Mode;
        var kokoroService = GetImageAnalysisKokoroSpeechService();
        var installed = kokoroService.IsModelInstalled(_appSettings.LanguageCode);
        var canReplay = ImageAnalysisSpeechTextService
            .BuildSegments(_imageAnalysisLiterarySession?.ReviewSummary)
            .Count > 0;
        status ??= mode switch
        {
            ImageAnalysisSpeechModes.Kokoro when !installed =>
                L("ImageAnalysis.Workspace.Voice.ModelMissing"),
            ImageAnalysisSpeechModes.Kokoro when kokoroService.IsWarm(_appSettings.LanguageCode) =>
                L("ImageAnalysis.Workspace.Voice.KokoroReady"),
            ImageAnalysisSpeechModes.Programmatic =>
                L("ImageAnalysis.Workspace.Voice.ProgrammaticReady"),
            _ => string.Empty
        };
        ImageAnalysisWorkspacePage.SetSpeechState(settings, installed, canReplay, status, isBusy);
        RefreshSessionAudioUi();
    }

    private async void ImageAnalysisWorkspacePage_SpeechModeRequested(
        object? sender,
        ImageAnalysisSpeechModeRequestedEventArgs e)
    {
        CancelImageAnalysisSpeech();
        if (IsHeavyImageAnalysis)
        {
            var heavySettings = GetHeavyImageAnalysisSpeechSettings();
            heavySettings.Mode = e.Mode;
            _appSettingsStore.Save(_appSettings);
            RefreshImageAnalysisSpeechUi();
            if (e.Mode == ImageAnalysisSpeechModes.Kokoro
                && GetImageAnalysisKokoroSpeechService().IsModelInstalled(_appSettings.LanguageCode))
            {
                await WarmImageAnalysisSpeechAsync(forceMemoryAttempt: true);
            }
            if (e.Mode != ImageAnalysisSpeechModes.Off
                && ImageAnalysisSpeechTextService.BuildSegments(_imageAnalysisLiterarySession?.ReviewSummary).Count > 0)
            {
                await SpeakCurrentImageAnalysisSummaryAsync(automatic: false);
            }
            return;
        }
        _appSettings.ImageAnalysisSpeech ??= new ImageAnalysisSpeechSettings();
        _appSettings.ImageAnalysisSpeech.Mode = e.Mode;
        _appSettingsStore.Save(_appSettings);
        RefreshImageAnalysisSpeechUi();

        if (e.Mode == ImageAnalysisSpeechModes.Kokoro
            && GetImageAnalysisKokoroSpeechService().IsModelInstalled(_appSettings.LanguageCode))
        {
            await WarmImageAnalysisSpeechAsync(forceMemoryAttempt: true);
        }

        if (e.Mode != ImageAnalysisSpeechModes.Off
            && ImageAnalysisSpeechTextService.BuildSegments(_imageAnalysisLiterarySession?.ReviewSummary).Count > 0)
        {
            await SpeakCurrentImageAnalysisSummaryAsync(automatic: false);
        }
    }

    private async void ImageAnalysisWorkspacePage_KokoroDownloadRequested(object? sender, EventArgs e)
    {
        if (_managedModelAcquisition is null
            || _managedModelOperationActive
            || _imageAnalysisVoiceDownloadCts is not null)
        {
            return;
        }

        _imageAnalysisBundleInstallationService.Check(_storageSettings);
        var artifactId = ManagedModelCatalog.ResolveKokoroArtifactId(_appSettings.LanguageCode);
        var card = _imageAnalysisBundleInstallationService.LibraryStore.Load(artifactId);
        if (card is null)
        {
            RefreshImageAnalysisSpeechUi(L("ImageAnalysis.Workspace.Voice.DownloadFailed"));
            return;
        }

        var confirmation = WpfMessageBox.Show(
            this,
            LF(
                "ImageAnalysis.Workspace.Voice.DownloadConfirm",
                card.DisplayName,
                ComponentCardViewModel.FormatBytes(card.TotalBytes),
                card.InstallDirectory,
                card.RepositoryId,
                card.License),
            L("ImageAnalysis.Workspace.Voice.DownloadTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        _imageAnalysisVoiceDownloadCts = new CancellationTokenSource();
        var progress = new Progress<ManagedModelDownloadProgress>(value =>
        {
            var message = LF(
                "ImageAnalysis.Workspace.Voice.DownloadProgress",
                ComponentCardViewModel.FormatBytes(value.DownloadedBytes),
                ComponentCardViewModel.FormatBytes(value.TotalBytes));
            RefreshImageAnalysisSpeechUi(message, isBusy: true);
        });
        try
        {
            RefreshImageAnalysisSpeechUi(
                L("ImageAnalysis.Workspace.Voice.Downloading"),
                isBusy: true);
            await _managedModelAcquisition.DownloadAsync(
                artifactId,
                progress,
                _imageAnalysisVoiceDownloadCts.Token);
            RefreshManagedModels();
            RefreshImageAnalysisSpeechUi(L("ImageAnalysis.Workspace.Voice.DownloadComplete"));
            await WarmImageAnalysisSpeechAsync(forceMemoryAttempt: false);
        }
        catch (OperationCanceledException)
        {
            RefreshImageAnalysisSpeechUi(L("ImageAnalysis.Workspace.Voice.DownloadCancelled"));
        }
        catch (Exception ex)
        {
            LogImageAnalysisRuntime($"Kokoro download failed: {ex.Message}");
            RefreshImageAnalysisSpeechUi(L("ImageAnalysis.Workspace.Voice.DownloadFailed"));
        }
        finally
        {
            _imageAnalysisVoiceDownloadCts.Dispose();
            _imageAnalysisVoiceDownloadCts = null;
        }
    }

    private void ImageAnalysisWorkspacePage_SpeechSettingsChanged(
        object? sender,
        ImageAnalysisSpeechSettingsChangedEventArgs e)
    {
        if (IsHeavyImageAnalysis)
        {
            var settings = GetHeavyImageAnalysisSpeechSettings();
            if (e.Mode == ImageAnalysisSpeechModes.Omni)
            {
                settings.OmniVolume = e.Volume;
                settings.OmniRatePercent = e.RatePercent;
            }
            else if (e.Mode == ImageAnalysisSpeechModes.Kokoro)
            {
                settings.KokoroVolume = e.Volume;
                settings.KokoroRatePercent = e.RatePercent;
            }
            else if (e.Mode == ImageAnalysisSpeechModes.Programmatic)
            {
                settings.ProgrammaticVolume = e.Volume;
                settings.ProgrammaticRatePercent = e.RatePercent;
            }
            else
            {
                return;
            }
            _appSettingsStore.Save(_appSettings);
            return;
        }
        _appSettings.ImageAnalysisSpeech ??= new ImageAnalysisSpeechSettings();
        if (e.Mode == ImageAnalysisSpeechModes.Kokoro)
        {
            _appSettings.ImageAnalysisSpeech.KokoroVolume = e.Volume;
            _appSettings.ImageAnalysisSpeech.KokoroRatePercent = e.RatePercent;
        }
        else if (e.Mode == ImageAnalysisSpeechModes.Programmatic)
        {
            _appSettings.ImageAnalysisSpeech.ProgrammaticVolume = e.Volume;
            _appSettings.ImageAnalysisSpeech.ProgrammaticRatePercent = e.RatePercent;
        }
        else
        {
            return;
        }

        _appSettingsStore.Save(_appSettings);
    }

    private void ImageAnalysisWorkspacePage_OmniSpeakerChanged(
        object? sender,
        ImageAnalysisOmniSpeakerChangedEventArgs e)
    {
        if (!IsHeavyImageAnalysis)
        {
            return;
        }
        GetHeavyImageAnalysisSpeechSettings().OmniSpeaker = e.Speaker;
        _appSettingsStore.Save(_appSettings);
    }

    private async void ImageAnalysisWorkspacePage_ReplaySpeechRequested(object? sender, EventArgs e)
    {
        var mode = IsHeavyImageAnalysis ? GetHeavyImageAnalysisSpeechSettings().Mode : _appSettings.ImageAnalysisSpeech?.Mode;
        if (mode == ImageAnalysisSpeechModes.Kokoro) { _sessionAudioPlayer?.Toggle(); return; }
        await SpeakCurrentImageAnalysisSummaryAsync(automatic: false);
    }

    private Task BeginImageAnalysisSpeechWarmup()
    {
        _imageAnalysisSpeechWarmupCts?.Cancel();
        _imageAnalysisSpeechWarmupCts?.Dispose();
        _imageAnalysisSpeechWarmupCts = new CancellationTokenSource();
        return WarmImageAnalysisSpeechAsync(
            forceMemoryAttempt: false,
            _imageAnalysisSpeechWarmupCts.Token);
    }

    private async Task WarmImageAnalysisSpeechAsync(
        bool forceMemoryAttempt,
        CancellationToken cancellationToken = default, BackgroundOperationState? restored = null)
    {
        if (!GetImageAnalysisKokoroSpeechService().IsModelInstalled(_appSettings.LanguageCode))
        {
            RefreshImageAnalysisSpeechUi();
            return;
        }

        var currentMode = IsHeavyImageAnalysis
            ? GetHeavyImageAnalysisSpeechSettings().Mode
            : _appSettings.ImageAnalysisSpeech?.Mode ?? ImageAnalysisSpeechModes.Off;
        if (currentMode == ImageAnalysisSpeechModes.Kokoro)
        {
            RefreshImageAnalysisSpeechUi(
                L("ImageAnalysis.Workspace.Voice.Preparing"),
                isBusy: true);
        }

        var preparation = new ImagePreparationBackgroundInput(_imageAnalysisLiterarySession?.BundleId
            ?? _selectedImageAnalysisBundle?.Id ?? ImageAnalysisBundleCatalog.MediumId,
            _imageAnalysisLiterarySession?.SessionId, true, forceMemoryAttempt, ActiveImageStorage,
            _imageAnalysisLiterarySession, CaptureBackgroundSpeechOptions());
        var language = restored?.Input.Deserialize<ImagePreparationBackgroundInput>()?.Speech?.Language ?? preparation.Speech!.Language;
        var result = await ApplicationBackgroundOperations.RunAsync(ImagePreparationBackgroundKind,
            L("ImageAnalysis.Workspace.Voice.Preparing"), preparation.SessionId, preparation,
            attempt => GetImageAnalysisKokoroSpeechService().WarmAsync(language,
                forceMemoryAttempt, pendingAllocationBytes: 0, attempt), cancellationToken, restored);
        var memory = result.Memory;
        var status = result.Code switch
        {
            KokoroWarmupCodes.Ready or KokoroWarmupCodes.AlreadyReady =>
                L("ImageAnalysis.Workspace.Voice.KokoroReady"),
            KokoroWarmupCodes.InsufficientMemory =>
                L("ImageAnalysis.Workspace.Voice.LowMemory"),
            KokoroWarmupCodes.RuntimeMissing =>
                L("ImageAnalysis.Workspace.Voice.RuntimeMissing"),
            KokoroWarmupCodes.ModelMissing =>
                L("ImageAnalysis.Workspace.Voice.ModelMissing"),
            KokoroWarmupCodes.Cancelled => string.Empty,
            _ => L("ImageAnalysis.Workspace.Voice.Failed")
        };
        if ((IsHeavyImageAnalysis
                ? GetHeavyImageAnalysisSpeechSettings().Mode
                : _appSettings.ImageAnalysisSpeech?.Mode ?? ImageAnalysisSpeechModes.Off)
            == ImageAnalysisSpeechModes.Kokoro)
        {
            RefreshImageAnalysisSpeechUi(status);
            await System.Windows.Threading.Dispatcher.Yield(
                System.Windows.Threading.DispatcherPriority.Render);
        }
        else
        {
            RefreshImageAnalysisSpeechUi();
        }

        LogImageAnalysisRuntime(
            $"Kokoro warmup: {result.Code}; language={NormalizeSpeechLanguage(_appSettings.LanguageCode)}; " +
            $"placement=CPU/RAM; load={result.LoadMilliseconds} ms; " +
            $"peakRam={result.PeakWorkingSetBytes} bytes; cpuAvg={result.AverageCpuPercent:F1}%; " +
            $"cpuPeak={result.PeakCpuPercent:F1}%; " +
            $"memoryAvailable={memory?.AvailableBytes ?? 0} bytes; " +
            $"memoryExpected={memory?.ExpectedRuntimeBytes ?? 0} bytes; " +
            $"memoryPending={memory?.PendingAllocationBytes ?? 0} bytes; " +
            $"memorySafetyReserve={memory?.SafetyReserveBytes ?? 0} bytes; " +
            $"memoryRequired={memory?.RequiredBytes ?? 0} bytes; " +
            $"errorStage={DiagnosticValue(result.ErrorStage)}; " +
            $"errorType={DiagnosticValue(result.ErrorType)}; " +
            $"error={DiagnosticValue(result.Error)}; " +
            $"stderr={DiagnosticValue(result.StandardErrorTail)}.");
        if (result.IsReady)
        {
            LogImageAnalysisRuntime(GetImageAnalysisKokoroSpeechService().DescribeCurrentLaunch(
                _appSettings.LanguageCode));
            LogImageAnalysisRuntime(GetImageAnalysisKokoroSpeechService().DescribeCurrentRuntime(
                _appSettings.LanguageCode,
                "warm"));
        }
    }

    private ImageBackgroundSpeechOptions CaptureBackgroundSpeechOptions()
    {
        var settings = _appSettings.ImageAnalysisSpeech ?? new ImageAnalysisSpeechSettings();
        var heavy = IsHeavyImageAnalysis ? GetHeavyImageAnalysisSpeechSettings() : null;
        var input = new ImageBackgroundSpeechOptions(_appSettings.LanguageCode, heavy?.Mode ?? settings.Mode,
            settings, heavy, _appSettings.CoreVoice ?? new CoreVoiceSettings());
        return JsonSerializer.Deserialize<ImageBackgroundSpeechOptions>(JsonSerializer.Serialize(input))!;
    }

    private async Task SpeakCurrentImageAnalysisSummaryAsync(
        bool automatic,
        Action? playbackStarted = null, CancellationToken operationToken = default,
        BackgroundOperationState? restored = null, ImageBackgroundSpeechOptions? options = null)
    {
        var playbackSignal = 0;
        void SignalPlaybackStarted()
        {
            if (Interlocked.Exchange(ref playbackSignal, 1) == 0)
            {
                playbackStarted?.Invoke();
            }
        }

        var session = _imageAnalysisLiterarySession;
        if (session is null)
        {
            SignalPlaybackStarted();
            return;
        }
        var segments = ImageAnalysisSpeechTextService.BuildSegments(session.ReviewSummary);
        if (segments.Count == 0)
        {
            SignalPlaybackStarted();
            return;
        }
        options ??= restored?.Input.Deserialize<ImageSpeechBackgroundInput>()?.Options ?? CaptureBackgroundSpeechOptions();
        var mode = options.Mode;
        if (mode == ImageAnalysisSpeechModes.Off)
        {
            SignalPlaybackStarted();
            return;
        }
        var fingerprint = ImageAnalysisSpeechTextService.CreateFingerprint(session.ReviewSummary);
        if (automatic && string.Equals(
                fingerprint,
                _lastAutoSpokenImageAnalysisFingerprint,
                StringComparison.Ordinal))
        {
            SignalPlaybackStarted();
            return;
        }

        CancelImageAnalysisSpeech();
        if (mode == ImageAnalysisSpeechModes.Kokoro) _sessionAudioPlayer?.Clear();
        var owner = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
        _imageAnalysisSpeechCts = owner;
        var cancellationToken = owner.Token;
        RefreshImageAnalysisSpeechUi(
            mode == ImageAnalysisSpeechModes.Kokoro
                ? GetImageAnalysisKokoroSpeechService().IsWarm(_appSettings.LanguageCode)
                    ? L("ImageAnalysis.Workspace.Voice.Synthesizing")
                    : L("ImageAnalysis.Workspace.Voice.Preparing")
                : L("ImageAnalysis.Workspace.Voice.Speaking"),
            isBusy: true);
        try
        {
            // Retired routing, preserved for restoration:
            // mode == ImageAnalysisSpeechModes.Omni
            //     ? await SpeakImageAnalysisWithOmniAsync(segments, SignalPlaybackStarted, cancellationToken)
            var completed = await ApplicationBackgroundOperations.RunAsync(ImageSpeechBackgroundKind,
                L("ImageAnalysis.Workspace.Voice.Synthesizing"), session.SessionId,
                new ImageSpeechBackgroundInput(session, _batchStorage ?? ActiveImageStorage, options), async attempt =>
                {
                    var success = mode == ImageAnalysisSpeechModes.Kokoro
                        ? await SpeakImageAnalysisWithKokoroAsync(segments, SignalPlaybackStarted, attempt, options)
                        : await SpeakImageAnalysisProgrammaticallyAsync(segments, SignalPlaybackStarted, attempt, false, options);
                    attempt.ThrowIfCancellationRequested();
                    if (!success) throw new BackgroundOperationWaitingException("Tray.NeedsInput");
                    return true;
                }, cancellationToken, restored);
            if (completed)
            {
                _lastAutoSpokenImageAnalysisFingerprint = fingerprint;
                RefreshImageAnalysisSpeechUi(L("ImageAnalysis.Workspace.Voice.Ready"));
            }
        }
        catch (OperationCanceledException)
        {
            if (operationToken.IsCancellationRequested) throw;
            if (IsHeavyImageAnalysis && _imageAnalysisLiterarySession is { } cancelledSession)
            {
                var heavySettings = GetHeavyImageAnalysisSpeechSettings();
                cancelledSession.SpeechResult = CreateHeavySpeechFailure(
                    mode,
                    heavySettings,
                    error: string.Empty,
                    cancelled: true);
                _imageAnalysisSessionStore.Save(cancelledSession, ActiveImageStorage);
            }
            RefreshImageAnalysisSpeechUi();
        }
        catch (Exception ex)
        {
            if (operationToken.CanBeCanceled) throw;
            LogImageAnalysisRuntime(
                $"Heavy voice error: requested={mode}; fallback=false; " +
                $"type={ex.GetType().Name}; error={DiagnosticValue(ex.Message)}.");
            if (IsHeavyImageAnalysis && _imageAnalysisLiterarySession is { } failedSession)
            {
                var heavySettings = GetHeavyImageAnalysisSpeechSettings();
                failedSession.SpeechResult = CreateHeavySpeechFailure(
                    mode,
                    heavySettings,
                    ex.Message,
                    cancelled: false);
                _imageAnalysisSessionStore.Save(failedSession, ActiveImageStorage);
                ShowHeavySpeechError(string.IsNullOrWhiteSpace(ex.Message)
                    ? L("ImageAnalysis.Workspace.HeavyVoice.Error")
                    : ex.Message);
            }
            else
            {
                RefreshImageAnalysisSpeechUi(L("ImageAnalysis.Workspace.Voice.Failed"));
            }
        }
        finally
        {
            SignalPlaybackStarted();
            if (ReferenceEquals(_imageAnalysisSpeechCts, owner))
            {
                _imageAnalysisSpeechCts = null;
            }
            owner.Dispose();
        }
    }

    private static ImageAnalysisSpeechResult CreateHeavySpeechFailure(
        string mode,
        ImageAnalysisHeavySpeechSettings settings,
        string error,
        bool cancelled) => new()
        {
            RequestedMode = mode,
            ActualProvider = mode,
            Speaker = mode == ImageAnalysisSpeechModes.Omni ? settings.OmniSpeaker : string.Empty,
            Volume = mode switch
            {
                ImageAnalysisSpeechModes.Omni => settings.OmniVolume,
                ImageAnalysisSpeechModes.Kokoro => settings.KokoroVolume,
                _ => settings.ProgrammaticVolume
            },
            RatePercent = mode switch
            {
                ImageAnalysisSpeechModes.Omni => settings.OmniRatePercent,
                ImageAnalysisSpeechModes.Kokoro => settings.KokoroRatePercent,
                _ => settings.ProgrammaticRatePercent
            },
            Completed = false,
            Cancelled = cancelled,
            AutomaticFallbackUsed = false,
            Error = error
        };

    private async Task<bool> SpeakImageAnalysisWithKokoroAsync(
        IReadOnlyList<CoreSpeechSegment> segments,
        Action playbackStarted,
        CancellationToken cancellationToken, ImageBackgroundSpeechOptions? options = null)
    {
        options ??= CaptureBackgroundSpeechOptions();
        if (!GetImageAnalysisKokoroSpeechService().IsModelInstalled(options.Language))
        {
            var message = L("ImageAnalysis.Workspace.Voice.ModelMissing");
            if (IsHeavyImageAnalysis)
            {
                if (_imageAnalysisLiterarySession is { } session)
                {
                    session.SpeechResult = CreateHeavySpeechFailure(
                        ImageAnalysisSpeechModes.Kokoro,
                        GetHeavyImageAnalysisSpeechSettings(),
                        message,
                        cancelled: false);
                    _imageAnalysisSessionStore.Save(session, ActiveImageStorage);
                }
                ShowHeavySpeechError(message);
            }
            else
            {
                RefreshImageAnalysisSpeechUi(message);
            }
            return false;
        }

        var progress = new Progress<KokoroSpeechProgress>(value =>
        {
            if (value.Stage == KokoroSpeechStages.Playing)
            {
                playbackStarted();
            }
            var status = value.Stage switch
            {
                KokoroSpeechStages.Warming => L("ImageAnalysis.Workspace.Voice.Preparing"),
                KokoroSpeechStages.Synthesizing => L("ImageAnalysis.Workspace.Voice.Synthesizing"),
                KokoroSpeechStages.Playing => L("ImageAnalysis.Workspace.Voice.Speaking"),
                _ => L("ImageAnalysis.Workspace.Voice.Speaking")
            };
            RefreshImageAnalysisSpeechUi(status, isBusy: true);
        });
        var heavySettings = options.Heavy;
        var cachePath = BackgroundSpeechCache.PathFor(string.Join(Environment.NewLine, segments.Select(s => s.Text)),
            options.Language, heavySettings?.KokoroVolume ?? options.Settings.KokoroVolume,
            heavySettings?.KokoroRatePercent ?? options.Settings.KokoroRatePercent);
        if (cachePath is not null && File.Exists(cachePath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetSessionAudioPlayer().Open(cachePath, deleteOnClear: false); playbackStarted(); return true;
        }
        var result = await GetImageAnalysisKokoroSpeechService().SpeakAsync(
            options.Language,
            string.Join(Environment.NewLine, segments.Select(segment => segment.Text)),
            heavySettings?.KokoroVolume ?? options.Settings.KokoroVolume,
            heavySettings?.KokoroRatePercent ?? options.Settings.KokoroRatePercent,
            progress,
            cancellationToken, generateOnly: true);
        if (result.Completed && !string.IsNullOrWhiteSpace(result.AudioPath))
        {
            if (cachePath is not null) BackgroundSpeechCache.Save(result.AudioPath, cachePath);
            if (cancellationToken.IsCancellationRequested) { System.IO.File.Delete(result.AudioPath); return false; }
            if (cachePath is not null) File.Delete(result.AudioPath);
            GetSessionAudioPlayer().Open(cachePath ?? result.AudioPath, deleteOnClear: cachePath is null);
            playbackStarted();
        }
        LogImageAnalysisRuntime(
            $"Kokoro speech: {result.Code}; language={NormalizeSpeechLanguage(options.Language)}; " +
            $"requestedEngine=kokoro; generation={result.GenerationMilliseconds} ms; " +
            $"firstAudio={result.TimeToFirstAudioMilliseconds} ms; peak={result.PeakWorkingSetBytes} bytes; " +
            $"cpu={result.CpuMilliseconds:F0} ms; cpuAvg={result.AverageCpuPercent:F1}%; " +
            $"cpuPeak={result.PeakCpuPercent:F1}%; errorStage={DiagnosticValue(result.ErrorStage)}; " +
            $"errorType={DiagnosticValue(result.ErrorType)}; error={DiagnosticValue(result.Error)}; " +
            $"stderr={DiagnosticValue(result.StandardErrorTail)}.");
        LogImageAnalysisRuntime(GetImageAnalysisKokoroSpeechService().DescribeCurrentRuntime(
            options.Language,
            result.Completed ? "after_speech" : "speech_failed"));
        if (IsHeavyImageAnalysis && _imageAnalysisLiterarySession is { } measuredSession)
        {
            // Previously only the JSONL log retained these values; the session showed zero.
            measuredSession.RuntimeMetrics.SpeechMilliseconds = result.GenerationMilliseconds;
            measuredSession.RuntimeMetrics.TimeToFirstAudioMilliseconds = result.TimeToFirstAudioMilliseconds;
        }
        if (result.Completed)
        {
            if (IsHeavyImageAnalysis && _imageAnalysisLiterarySession is { } heavySession)
            {
                heavySession.SpeechResult = new ImageAnalysisSpeechResult
                {
                    RequestedMode = ImageAnalysisSpeechModes.Kokoro,
                    ActualProvider = ImageAnalysisSpeechModes.Kokoro,
                    Volume = heavySettings?.KokoroVolume ?? 100,
                    RatePercent = heavySettings?.KokoroRatePercent ?? 100,
                    SynthesisMilliseconds = result.GenerationMilliseconds,
                    TimeToFirstAudioMilliseconds = result.TimeToFirstAudioMilliseconds,
                    Completed = true,
                    AutomaticFallbackUsed = false
                };
                _imageAnalysisSessionStore.Save(heavySession, ActiveImageStorage);
            }
            var actualVoice = NormalizeSpeechLanguage(options.Language) == "en"
                ? "af_heart"
                : "sveta";
            LogImageAnalysisRuntime(
                $"Voice playback completed: requested=kokoro; actual=kokoro; " +
                $"voice={actualVoice}; device=CPU; " +
                $"language={NormalizeSpeechLanguage(options.Language)}.");
            return true;
        }
        if (result.Code == KokoroWarmupCodes.Cancelled)
        {
            return false;
        }

        if (IsHeavyImageAnalysis)
        {
            if (_imageAnalysisLiterarySession is { } heavySession)
            {
                heavySession.SpeechResult = new ImageAnalysisSpeechResult
                {
                    RequestedMode = ImageAnalysisSpeechModes.Kokoro,
                    ActualProvider = ImageAnalysisSpeechModes.Kokoro,
                    Volume = heavySettings?.KokoroVolume ?? 100,
                    RatePercent = heavySettings?.KokoroRatePercent ?? 100,
                    Completed = false,
                    AutomaticFallbackUsed = false,
                    Error = result.Error
                };
                _imageAnalysisSessionStore.Save(heavySession, ActiveImageStorage);
            }
            ShowHeavySpeechError(string.IsNullOrWhiteSpace(result.Error)
                ? L("ImageAnalysis.Workspace.HeavyVoice.Error")
                : result.Error);
            return false;
        }

        RefreshImageAnalysisSpeechUi(
            L("ImageAnalysis.Workspace.Voice.Fallback"),
            isBusy: true);
        LogImageAnalysisRuntime(
            $"Voice fallback: requested=kokoro; actual=programmatic; " +
            $"language={NormalizeSpeechLanguage(options.Language)}; " +
            $"reason={DiagnosticValue(result.Error)}.");
        return await SpeakImageAnalysisProgrammaticallyAsync(
            segments,
            playbackStarted,
            cancellationToken,
            fallbackFromKokoro: true, options);
    }

#if false // Built-in speech retired from this scenario; working implementation kept for restoration.
    private async Task<bool> SpeakImageAnalysisWithOmniAsync(
        IReadOnlyList<CoreSpeechSegment> segments,
        Action playbackStarted,
        CancellationToken cancellationToken)
    {
        var session = _imageAnalysisLiterarySession;
        var settings = GetHeavyImageAnalysisSpeechSettings();
        if (session is null
            || GetImageAnalysisLiteraryPipeline(session) is not IOmniSpeechPipeline omni)
        {
            ShowHeavySpeechError(L("ImageAnalysis.Workspace.HeavyVoice.Error"));
            return false;
        }
        var text = string.Join(Environment.NewLine, segments.Select(segment => segment.Text));
        var progress = new Progress<OmniSpeechProgress>(value =>
        {
            RefreshImageAnalysisSpeechUi(
                value.Stage == "ready"
                    ? L("ImageAnalysis.Workspace.HeavyVoice.AudioReady")
                    : L("ImageAnalysis.Workspace.Voice.Synthesizing"),
                isBusy: true);
        });
        var result = await omni.SpeakAsync(
            text,
            settings.OmniSpeaker,
            settings.OmniVolume,
            settings.OmniRatePercent,
            progress,
            cancellationToken);
        session.SpeechResult = new ImageAnalysisSpeechResult
        {
            RequestedMode = ImageAnalysisSpeechModes.Omni,
            ActualProvider = ImageAnalysisSpeechModes.Omni,
            Speaker = settings.OmniSpeaker,
            Volume = settings.OmniVolume,
            RatePercent = settings.OmniRatePercent,
            TemporaryAudioPath = result.AudioPath,
            SynthesisMilliseconds = result.GenerationMilliseconds,
            TimeToFirstAudioMilliseconds = result.TimeToFirstAudioMilliseconds,
            Completed = result.Completed,
            AutomaticFallbackUsed = false,
            Error = result.Error
        };
        _imageAnalysisSessionStore.Save(session, ActiveImageStorage);
        if (!result.Completed || string.IsNullOrWhiteSpace(result.AudioPath))
        {
            ShowHeavySpeechError(string.IsNullOrWhiteSpace(result.Error)
                ? L("ImageAnalysis.Workspace.HeavyVoice.Error")
                : result.Error);
            return false;
        }

        playbackStarted();
        RefreshImageAnalysisSpeechUi(L("ImageAnalysis.Workspace.Voice.Speaking"), isBusy: true);
        _imageAnalysisOmniPlayer?.Stop();
        _imageAnalysisOmniPlayer?.Dispose();
        var player = new SoundPlayer(result.AudioPath);
        _imageAnalysisOmniPlayer = player;
        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                player.Stop();
            }
            catch
            {
            }
        });
        await Task.Run(player.PlaySync, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        LogImageAnalysisRuntime(
            $"Omni speech completed: speaker={settings.OmniSpeaker}; generation={result.GenerationMilliseconds} ms; firstAudio={result.TimeToFirstAudioMilliseconds} ms; fallback=false.");
        return true;
    }
#endif

    private void ShowHeavySpeechError(string message)
    {
        if (_imageAnalysisLiterarySession is { } session)
        {
            ImageAnalysisWorkspacePage.RevealReviewSummary(session);
        }
        ImageAnalysisWorkspacePage.ShowSpeechError(message);
        RefreshImageAnalysisSpeechUi(L("ImageAnalysis.Workspace.HeavyVoice.Error"));
    }

    private async Task<bool> SpeakImageAnalysisProgrammaticallyAsync(
        IReadOnlyList<CoreSpeechSegment> segments,
        Action playbackStarted,
        CancellationToken cancellationToken,
        bool fallbackFromKokoro, ImageBackgroundSpeechOptions? options = null)
    {
        options ??= CaptureBackgroundSpeechOptions();
        var configured = options.Core;
        var speechSettings = options.Settings;
        var heavySettings = options.Heavy;
        var programmaticVolume = heavySettings?.ProgrammaticVolume ?? speechSettings.ProgrammaticVolume;
        var programmaticRate = heavySettings?.ProgrammaticRatePercent ?? speechSettings.ProgrammaticRatePercent;
        var settings = new CoreVoiceSettings
        {
            Enabled = true,
            Provider = configured.Provider,
            Volume = Math.Clamp(programmaticVolume * 2, 0, 200),
            Rate = Math.Clamp(
                (int)Math.Round(120d * programmaticRate / 100d),
                80,
                240),
            RussianVoice = configured.RussianVoice,
            EnglishVoice = configured.EnglishVoice
        };
        var language = NormalizeSpeechLanguage(options.Language);
        var usesRhVoice = string.Equals(
                configured.Provider,
                CoreVoiceSettings.RhVoiceProvider,
                StringComparison.OrdinalIgnoreCase)
            && _imageAnalysisProgrammaticSpeechCoordinator.IsRhVoiceAvailable;
        var actualProvider = usesRhVoice ? "RHVoice" : "eSpeak NG";
        var actualVoice = usesRhVoice
            ? language == "en" ? "Bdl" : "Elena"
            : language == "en" ? settings.EnglishVoice : settings.RussianVoice;
        LogImageAnalysisRuntime(
            $"Voice playback started: requested={(fallbackFromKokoro ? "kokoro" : "programmatic")}; " +
            $"actual=programmatic; provider={actualProvider}; voice={actualVoice}; " +
            $"language={language}; fallbackFromKokoro={fallbackFromKokoro}.");
        playbackStarted();
        var result = await _imageAnalysisProgrammaticSpeechCoordinator.PresentAsync(
            new CoreSpeechRequest(
                segments,
                options.Language,
                settings,
                "image_analysis_review_summary",
                SpeechRoles.UncertaintyExecutor),
            new Progress<CoreSpeechProgress>(_ => { }),
            _coreSessionLog,
            cancellationToken);
        if (!result.Completed && !result.Skipped)
        {
            if (IsHeavyImageAnalysis)
            {
                ShowHeavySpeechError(L("ImageAnalysis.Workspace.HeavyVoice.Error"));
            }
            else
            {
                RefreshImageAnalysisSpeechUi(L("ImageAnalysis.Workspace.Voice.Failed"));
            }
        }
        if (IsHeavyImageAnalysis && _imageAnalysisLiterarySession is { } heavySession)
        {
            heavySession.SpeechResult = new ImageAnalysisSpeechResult
            {
                RequestedMode = ImageAnalysisSpeechModes.Programmatic,
                ActualProvider = ImageAnalysisSpeechModes.Programmatic,
                Volume = programmaticVolume,
                RatePercent = programmaticRate,
                Completed = result.Completed,
                Cancelled = result.Skipped,
                AutomaticFallbackUsed = false,
                Error = result.Completed ? string.Empty : result.ErrorCode ?? string.Empty
            };
            _imageAnalysisSessionStore.Save(heavySession, ActiveImageStorage);
        }
        LogImageAnalysisRuntime(
            $"Voice playback finished: actual=programmatic; provider={actualProvider}; " +
            $"voice={actualVoice}; language={language}; completed={result.Completed}; " +
            $"skipped={result.Skipped}; errorCode={DiagnosticValue(result.ErrorCode)}.");
        return result.Completed;
    }

    private static string NormalizeSpeechLanguage(string? languageCode) =>
        languageCode?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true ? "en" : "ru";

    private static string DiagnosticValue(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 800 ? normalized : normalized[..800] + "...";
    }

    private void CancelImageAnalysisSpeech()
    {
        _sessionAudioPlayer?.Pause();
        _imageAnalysisSpeechCts?.Cancel();
        _imageAnalysisProgrammaticSpeechCoordinator.Cancel();
        _imageAnalysisKokoroSpeechService?.StopPlayback();
        try
        {
            _imageAnalysisOmniPlayer?.Stop();
        }
        catch
        {
        }
    }

    private void StopImageAnalysisSpeechSession()
    {
        _sessionAudioPlayer?.Clear();
        CancelImageAnalysisSpeech();
        _imageAnalysisSpeechWarmupCts?.Cancel();
        _imageAnalysisSpeechWarmupCts?.Dispose();
        _imageAnalysisSpeechWarmupCts = null;
        _imageAnalysisVoiceDownloadCts?.Cancel();
        _imageAnalysisKokoroSpeechService?.Stop();
        _imageAnalysisOmniPlayer?.Dispose();
        _imageAnalysisOmniPlayer = null;
        _lastAutoSpokenImageAnalysisFingerprint = string.Empty;
    }

    private void DisposeImageAnalysisSpeech()
    {
        StopImageAnalysisSpeechSession();
        _imageAnalysisSpeechCts?.Dispose();
        _imageAnalysisSpeechCts = null;
        _imageAnalysisVoiceDownloadCts?.Dispose();
        _imageAnalysisVoiceDownloadCts = null;
        _imageAnalysisProgrammaticSpeechCoordinator.Dispose();
        _imageAnalysisKokoroSpeechService?.Dispose();
        _imageAnalysisKokoroSpeechService = null;
    }
}
