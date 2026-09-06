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

    private string HeavySpeechProfileKey =>
        (_imageAnalysisLiterarySession?.BundleId ?? _selectedImageAnalysisBundle?.Id) == ImageAnalysisBundleCatalog.LightId
            ? $"{ImageAnalysisBundleCatalog.LightId}|{ManagedModelCatalog.OmniAlphaRepository}|{ManagedModelCatalog.OmniAlphaRevision}"
            : $"{ImageAnalysisBundleCatalog.HeavyId}|{ManagedModelCatalog.Qwen25OmniRepository}|{ManagedModelCatalog.Qwen25OmniRevision}";

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
        CancellationToken cancellationToken = default)
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

        var result = await GetImageAnalysisKokoroSpeechService().WarmAsync(
            _appSettings.LanguageCode,
            forceMemoryAttempt,
            pendingAllocationBytes: 0,
            cancellationToken);
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

    private async Task SpeakCurrentImageAnalysisSummaryAsync(
        bool automatic,
        Action? playbackStarted = null)
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
        var mode = IsHeavyImageAnalysis
            ? GetHeavyImageAnalysisSpeechSettings().Mode
            : _appSettings.ImageAnalysisSpeech?.Mode ?? ImageAnalysisSpeechModes.Off;
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
        var owner = new CancellationTokenSource();
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
            var completed = mode == ImageAnalysisSpeechModes.Kokoro
                ? await SpeakImageAnalysisWithKokoroAsync(
                    segments,
                    SignalPlaybackStarted,
                    cancellationToken)
                : await SpeakImageAnalysisProgrammaticallyAsync(
                    segments,
                    SignalPlaybackStarted,
                    cancellationToken,
                    fallbackFromKokoro: false);
            if (completed)
            {
                _lastAutoSpokenImageAnalysisFingerprint = fingerprint;
                RefreshImageAnalysisSpeechUi(L("ImageAnalysis.Workspace.Voice.Ready"));
            }
        }
        catch (OperationCanceledException)
        {
            if (IsHeavyImageAnalysis && _imageAnalysisLiterarySession is { } cancelledSession)
            {
                var heavySettings = GetHeavyImageAnalysisSpeechSettings();
                cancelledSession.SpeechResult = CreateHeavySpeechFailure(
                    mode,
                    heavySettings,
                    error: string.Empty,
                    cancelled: true);
                _imageAnalysisSessionStore.Save(cancelledSession, _storageSettings);
            }
            RefreshImageAnalysisSpeechUi();
        }
        catch (Exception ex)
        {
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
                _imageAnalysisSessionStore.Save(failedSession, _storageSettings);
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
        CancellationToken cancellationToken)
    {
        if (!GetImageAnalysisKokoroSpeechService().IsModelInstalled(_appSettings.LanguageCode))
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
                    _imageAnalysisSessionStore.Save(session, _storageSettings);
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
        var heavySettings = IsHeavyImageAnalysis ? GetHeavyImageAnalysisSpeechSettings() : null;
        var result = await GetImageAnalysisKokoroSpeechService().SpeakAsync(
            _appSettings.LanguageCode,
            string.Join(Environment.NewLine, segments.Select(segment => segment.Text)),
            heavySettings?.KokoroVolume ?? _appSettings.ImageAnalysisSpeech?.KokoroVolume ?? 100,
            heavySettings?.KokoroRatePercent ?? _appSettings.ImageAnalysisSpeech?.KokoroRatePercent ?? 100,
            progress,
            cancellationToken, generateOnly: true);
        if (result.Completed && !string.IsNullOrWhiteSpace(result.AudioPath))
        {
            if (cancellationToken.IsCancellationRequested) { System.IO.File.Delete(result.AudioPath); return false; }
            GetSessionAudioPlayer().Open(result.AudioPath);
            playbackStarted();
        }
        LogImageAnalysisRuntime(
            $"Kokoro speech: {result.Code}; language={NormalizeSpeechLanguage(_appSettings.LanguageCode)}; " +
            $"requestedEngine=kokoro; generation={result.GenerationMilliseconds} ms; " +
            $"firstAudio={result.TimeToFirstAudioMilliseconds} ms; peak={result.PeakWorkingSetBytes} bytes; " +
            $"cpu={result.CpuMilliseconds:F0} ms; cpuAvg={result.AverageCpuPercent:F1}%; " +
            $"cpuPeak={result.PeakCpuPercent:F1}%; errorStage={DiagnosticValue(result.ErrorStage)}; " +
            $"errorType={DiagnosticValue(result.ErrorType)}; error={DiagnosticValue(result.Error)}; " +
            $"stderr={DiagnosticValue(result.StandardErrorTail)}.");
        LogImageAnalysisRuntime(GetImageAnalysisKokoroSpeechService().DescribeCurrentRuntime(
            _appSettings.LanguageCode,
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
                _imageAnalysisSessionStore.Save(heavySession, _storageSettings);
            }
            var actualVoice = NormalizeSpeechLanguage(_appSettings.LanguageCode) == "en"
                ? "af_heart"
                : "sveta";
            LogImageAnalysisRuntime(
                $"Voice playback completed: requested=kokoro; actual=kokoro; " +
                $"voice={actualVoice}; device=CPU; " +
                $"language={NormalizeSpeechLanguage(_appSettings.LanguageCode)}.");
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
                _imageAnalysisSessionStore.Save(heavySession, _storageSettings);
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
            $"language={NormalizeSpeechLanguage(_appSettings.LanguageCode)}; " +
            $"reason={DiagnosticValue(result.Error)}.");
        return await SpeakImageAnalysisProgrammaticallyAsync(
            segments,
            playbackStarted,
            cancellationToken,
            fallbackFromKokoro: true);
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
        _imageAnalysisSessionStore.Save(session, _storageSettings);
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
        bool fallbackFromKokoro)
    {
        var configured = _appSettings.CoreVoice ?? new CoreVoiceSettings();
        var speechSettings = _appSettings.ImageAnalysisSpeech ?? new ImageAnalysisSpeechSettings();
        var heavySettings = IsHeavyImageAnalysis ? GetHeavyImageAnalysisSpeechSettings() : null;
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
        var language = NormalizeSpeechLanguage(_appSettings.LanguageCode);
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
                _appSettings.LanguageCode,
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
            _imageAnalysisSessionStore.Save(heavySession, _storageSettings);
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
