using System.IO;
using AIHub.Models;

namespace AIHub.Services;

public sealed class OmniHeavySingleImageLiteraryPipeline :
    ISingleImageLiteraryPipeline,
    // IOmniSpeechPipeline, // Retired in this scenario; keep the Talker implementation below for restoration.
    IHeavyResourceMonitoringPipeline
{
    private readonly Qwen25OmniRuntimeService _runtime;
    private OmniWarmupResult? _warmup;

    public OmniHeavySingleImageLiteraryPipeline(Qwen25OmniRuntimeService runtime)
    {
        _runtime = runtime;
    }

    public string PipelineId => ImageAnalysisPipelineIds.OmniHeavy;

    public bool IsOmniReady => _runtime.IsReady;

    public async Task PrepareAsync(
        StorageSettings storageSettings,
        ImageAnalysisLiterarySession? session,
        bool prepareCoreConcurrently,
        Action<string> log,
        IProgress<ImageAnalysisLiteraryProgress>? progress,
        CancellationToken cancellationToken)
    {
        _warmup = await _runtime.PrepareAsync(log, progress, cancellationToken).ConfigureAwait(false);
        if (session is not null)
        {
            ApplyProvenance(session);
        }
    }

    public async Task<ImageAnalysisLiteraryResult> CreateAsync(
        ImageAnalysisFilePassport passport,
        ImageAnalysisLiterarySettings settings,
        StorageSettings storageSettings,
        ImageAnalysisLiterarySession session,
        Action<string> log,
        IProgress<ImageAnalysisLiteraryProgress>? progress,
        IProgress<ModelStreamChunk>? streamProgress,
        Action<ImageAnalysisPipelineCheckpoint>? checkpointReady,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(passport);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(session);
        OmniPromptPairAdapter.Validate(settings);
        await EnsureWorkerReadyAsync(session, log, progress, cancellationToken).ConfigureAwait(false);
        EnsureLanguage(session, settings.LanguageCode);
        ApplyProvenance(session);

        var conversation = new List<ImageAnalysisHiddenMessage>();
        var observationPrompt = ImageAnalysisOmniPromptBuilder.BuildObservationPrompt(settings);
        var observationRequest = Message("user", observationPrompt, includesImage: true);
        conversation.Add(observationRequest);
        streamProgress?.Report(new ModelStreamChunk(observationPrompt));
        progress?.Report(new ImageAnalysisLiteraryProgress(
            ManagedModelRoles.Core,
            "omni_observe",
            "Qwen2.5-Omni is describing the visible image."));
        var visual = await _runtime.GenerateAsync(
            "analyze",
            passport.SourcePath,
            conversation,
            streamProgress,
            cancellationToken,
            raw => SaveResponse(session, storageSettings, "analyze", conversation, raw, log), log).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(visual.Content))
        {
            throw new InvalidDataException("Omni returned an empty visual report.");
        }
        log(DescribeGeneration("visual", visual));
        conversation.Add(Message("assistant", visual.Content));
        checkpointReady?.Invoke(new ImageAnalysisPipelineCheckpoint(
            visual.Content,
            CloneConversation(conversation)));

        var composePrompt = ImageAnalysisOmniPromptBuilder.BuildComposePrompt(settings);
        conversation.Add(Message("user", composePrompt));
        streamProgress?.Report(new ModelStreamChunk(composePrompt));
        progress?.Report(new ImageAnalysisLiteraryProgress(
            ManagedModelRoles.Core,
            "omni_compose",
            "The same Qwen2.5-Omni conversation is verifying and composing the final result."));
        var composed = await _runtime.GenerateAsync(
            "compose",
            passport.SourcePath,
            conversation,
            streamProgress,
            cancellationToken,
            raw => SaveResponse(session, storageSettings, "compose", conversation, raw, log), log).ConfigureAwait(false);
        conversation.Add(Message("assistant", composed.Content));
        session.HiddenConversation = CloneConversation(conversation).ToList();
        session.AnalysisLanguageCode = NormalizeLanguage(settings.LanguageCode);
        session.RuntimeMetrics.VisualPassMilliseconds = visual.ElapsedMilliseconds;
        session.RuntimeMetrics.ComposePassMilliseconds = composed.ElapsedMilliseconds;
        log(DescribeGeneration("compose", composed));
        log($"Omni compose response received: chars={composed.Content.Length}; tokens={composed.GeneratedTokens}; composeMs={composed.ElapsedMilliseconds}.");
        var result = ParseSavedResponse(
            visual.Content,
            composed.Content,
            conversation,
            visual.ElapsedMilliseconds,
            composed.ElapsedMilliseconds,
            log);
        log($"Omni hidden chat completed: turns={conversation.Count}; visualMs={visual.ElapsedMilliseconds}; composeMs={composed.ElapsedMilliseconds}; visualTokens={visual.GeneratedTokens}; composeTokens={composed.GeneratedTokens}.");
        return result;
    }

    public async Task<string> ReviseAsync(
        ImageAnalysisLiterarySession session,
        string changeRequest,
        StorageSettings storageSettings,
        Action<string> log,
        IProgress<ImageAnalysisLiteraryProgress>? progress,
        IProgress<ModelStreamChunk>? streamProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(changeRequest);
        await EnsureWorkerReadyAsync(session, log, progress, cancellationToken).ConfigureAwait(false);
        if (session.File is null || string.IsNullOrWhiteSpace(session.VisualReport))
        {
            throw new InvalidOperationException("The Heavy session has no image or visual report to revise.");
        }
        EnsureLanguage(session, session.Settings.LanguageCode);
        if (session.HiddenConversation.Count < 4)
        {
            throw new InvalidDataException("The exact Heavy hidden conversation is unavailable for revision.");
        }

        var conversation = CloneConversation(session.HiddenConversation).ToList();
        var revisionPrompt = ImageAnalysisOmniPromptBuilder.BuildRevisionPrompt(
            session.Settings,
            changeRequest);
        conversation.Add(Message("user", revisionPrompt));
        streamProgress?.Report(new ModelStreamChunk(revisionPrompt));
        progress?.Report(new ImageAnalysisLiteraryProgress(
            ManagedModelRoles.Core,
            "omni_revise",
            "Qwen2.5-Omni is creating a new version in the same hidden conversation."));
        var revised = await _runtime.GenerateAsync(
            "revise",
            session.File.SourcePath,
            conversation,
            streamProgress,
            cancellationToken,
            raw => SaveResponse(session, storageSettings, "revise", conversation, raw, log), log).ConfigureAwait(false);
        conversation.Add(Message("assistant", revised.Content));
        var parsed = ParseSavedResponse(
            session.VisualReport,
            revised.Content,
            conversation,
            session.RuntimeMetrics.VisualPassMilliseconds,
            revised.ElapsedMilliseconds,
            log);
        session.HiddenConversation = CloneConversation(conversation).ToList();
        session.ReviewSummary = parsed.ReviewSummary;
        session.RuntimeMetrics.ComposePassMilliseconds = revised.ElapsedMilliseconds;
        log(DescribeGeneration("revise", revised));
        log($"Omni revision completed: turns={conversation.Count}; elapsedMs={revised.ElapsedMilliseconds}; tokens={revised.GeneratedTokens}.");
        return parsed.Description;
    }

#if false // Built-in speech retired from image analysis on 2026-09-04; do not delete.
    public async Task<OmniSpeechGenerationResult> SpeakAsync(
        string text,
        string speaker,
        int volume,
        int ratePercent,
        IProgress<OmniSpeechProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!_runtime.IsReady)
        {
            _warmup = await _runtime.PrepareAsync(
                _ => { },
                progress: null,
                cancellationToken,
                reuseCurrentPlan: true).ConfigureAwait(false);
        }
        return await _runtime.SpeakAsync(
            text,
            speaker,
            volume,
            ratePercent,
            progress,
            cancellationToken).ConfigureAwait(false);
    }
#endif

    public Task<ImageAnalysisHeavyResourceStatus> CaptureResourceStatusAsync(
        CancellationToken cancellationToken) =>
        _runtime.CaptureResourceStatusAsync(cancellationToken);

    public void Stop() => _runtime.Stop();

    public void Dispose() => _runtime.Dispose();

    private async Task EnsureWorkerReadyAsync(
        ImageAnalysisLiterarySession session,
        Action<string> log,
        IProgress<ImageAnalysisLiteraryProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (_runtime.IsReady)
        {
            return;
        }
        _warmup = await _runtime.PrepareAsync(
            log,
            progress,
            cancellationToken,
            reuseCurrentPlan: true).ConfigureAwait(false);
        ApplyProvenance(session);
    }

    private void ApplyProvenance(ImageAnalysisLiterarySession session)
    {
        session.BundleId = ImageAnalysisBundleCatalog.HeavyId;
        session.PipelineId = ImageAnalysisPipelineIds.OmniHeavy;
        session.PipelineVersion = ImageAnalysisPipelineIds.OmniHeavyVersion;
        session.ContractVersion = ImageAnalysisPipelineIds.ContractVersion;
        session.ModelId = ManagedModelCatalog.Qwen25OmniRepository;
        session.ModelRevision = ManagedModelCatalog.Qwen25OmniRevision;
        session.RuntimeId = ImageAnalysisRuntimeIds.Qwen25OmniTransformers;
        session.RuntimeVersion = _runtime.RuntimeVersion;
        var plan = _warmup?.Plan ?? _runtime.CurrentPlan;
        if (plan is not null)
        {
            session.Placement = plan.ToPlacementInfo();
            session.Placement.DeviceMapJson = _runtime.DeviceMapJson;
        }
        if (_warmup is not null)
        {
            session.RuntimeMetrics.WarmupMilliseconds = _warmup.LoadMilliseconds;
            session.RuntimeMetrics.PeakWorkingSetBytes = Math.Max(
                session.RuntimeMetrics.PeakWorkingSetBytes,
                _warmup.PeakWorkingSetBytes);
            session.RuntimeMetrics.RamBeforeWarmupBytes = _warmup.RamBeforeWarmupBytes;
            session.RuntimeMetrics.RamAfterWarmupBytes = _warmup.RamAfterWarmupBytes;
            session.RuntimeMetrics.CommitBeforeWarmupBytes = _warmup.CommitBeforeWarmupBytes;
            session.RuntimeMetrics.CommitAfterWarmupBytes = _warmup.CommitAfterWarmupBytes;
            session.RuntimeMetrics.VramBeforeWarmupBytes = _warmup.VramBeforeWarmupBytes;
            session.RuntimeMetrics.VramAfterWarmupBytes = _warmup.VramAfterWarmupBytes;
        }
    }

    private static void EnsureLanguage(ImageAnalysisLiterarySession session, string languageCode)
    {
        var normalized = NormalizeLanguage(languageCode);
        if (!string.IsNullOrWhiteSpace(session.AnalysisLanguageCode)
            && !string.Equals(session.AnalysisLanguageCode, normalized, StringComparison.Ordinal))
        {
            session.VisualReport = string.Empty;
            session.HiddenConversation.Clear();
        }
        session.AnalysisLanguageCode = normalized;
    }

    private static string NormalizeLanguage(string? languageCode) =>
        languageCode?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true ? "en" : "ru";

    private static ImageAnalysisHiddenMessage Message(
        string role,
        string content,
        bool includesImage = false) => new()
        {
            Role = role,
            Content = content,
            IncludesImage = includesImage
        };

    private static IReadOnlyList<ImageAnalysisHiddenMessage> CloneConversation(
        IEnumerable<ImageAnalysisHiddenMessage> messages) =>
        messages.Select(message => new ImageAnalysisHiddenMessage
        {
            Role = message.Role,
            Content = message.Content,
            IncludesImage = message.IncludesImage,
            CreatedAt = message.CreatedAt
        }).ToList();

    private static string DescribeGeneration(string stage, OmniTextGenerationResult result) =>
        $"Omni timing: stage={stage}; profile={result.RuntimeProfile}; attention={result.AttentionImplementation}; " +
        $"profileSwitchMs={result.ProfileSwitchMilliseconds}; preprocessMs={result.PreprocessingMilliseconds}; " +
        $"timeToFirstTokenMs={result.TimeToFirstTokenMilliseconds}; generationMs={result.GenerationMilliseconds}; " +
        $"totalMs={result.ElapsedMilliseconds}; inputTokens={result.InputTokens}; generatedTokens={result.GeneratedTokens}; " +
        $"finishReason={result.FinishReason}; eosTokenIds={result.EosTokenIds}; lastTokenId={result.LastTokenId}; " +
        $"decodeTokensPerSecond={result.DecodeTokensPerSecond:F3}.";

    private static void SaveResponse(ImageAnalysisLiterarySession session, StorageSettings storage,
        string stage, IReadOnlyList<ImageAnalysisHiddenMessage> conversation, string raw, Action<string> log)
    {
        var path = new ImageAnalysisSessionStore().SaveOmniResponse(session, storage, stage, conversation, raw);
        log($"Omni raw response saved before validation: stage={stage}; path={path}.");
    }

    private static ImageAnalysisLiteraryResult ParseSavedResponse(string visual, string response,
        IReadOnlyList<ImageAnalysisHiddenMessage> conversation, long visualMs, long composeMs, Action<string> log)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        try { return ImageAnalysisOmniResultParser.Parse(visual, response, conversation, visualMs, composeMs, log); }
        catch (InvalidDataException ex)
        {
            log($"Omni response format rejected; raw artifact retained: {ex.Message}");
            throw new ImageAnalysisOmniFormatException(ex);
        }
        finally { log($"Omni result parsing: elapsedMs={started.ElapsedMilliseconds}."); }
    }
}

public sealed class ImageAnalysisOmniFormatException(Exception inner)
    : Exception("The completed Omni response needs format recovery; original response is saved.", inner);
