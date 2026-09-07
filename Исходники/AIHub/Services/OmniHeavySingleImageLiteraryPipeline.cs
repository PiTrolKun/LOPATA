using System.IO;
using AIHub.Models;

namespace AIHub.Services;

public sealed class OmniHeavySingleImageLiteraryPipeline :
    ISingleImageLiteraryPipeline,
    // IOmniSpeechPipeline, // Retired in this scenario; keep the Talker implementation below for restoration.
    IHeavyResourceMonitoringPipeline
{
    private readonly IOmniTextRuntime _runtime;
    private OmniWarmupResult? _warmup;
    public IOmniTextRuntime TextRuntime => _runtime;

    public OmniHeavySingleImageLiteraryPipeline(IOmniTextRuntime runtime)
    {
        _runtime = runtime;
    }

    public string PipelineId => _runtime.PipelineId;

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
            "The selected model is describing the visible image."));
        var visual = await OmniResponseRecovery.RunAsync(async _ =>
        {
            var generated = await GenerateCheckedAsync(session,
                "analyze", passport.SourcePath, conversation, streamProgress, cancellationToken,
                raw => SaveResponse(session, storageSettings, "analyze", conversation, raw, log), log).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(generated.Content))
                throw new ImageAnalysisOmniFormatException(new InvalidDataException("Omni returned an empty visual report."));
            return generated;
        }, "analyze", log, progress, cancellationToken).ConfigureAwait(false);
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
            "The same model conversation is verifying and composing the final result."));
        var (composed, result) = await GenerateFinalAsync(session, storageSettings, "compose",
            passport.SourcePath, conversation, visual.Content, visual.ElapsedMilliseconds,
            log, progress, streamProgress, cancellationToken).ConfigureAwait(false);
        conversation.Add(Message("assistant", composed.Content));
        session.HiddenConversation = CloneConversation(conversation).ToList();
        session.AnalysisLanguageCode = NormalizeLanguage(settings.LanguageCode);
        session.RuntimeMetrics.VisualPassMilliseconds = visual.ElapsedMilliseconds;
        session.RuntimeMetrics.ComposePassMilliseconds = composed.ElapsedMilliseconds;
        log(DescribeGeneration("compose", composed));
        log($"Omni compose response received: chars={composed.Content.Length}; tokens={composed.GeneratedTokens}; composeMs={composed.ElapsedMilliseconds}.");
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
            "The selected model is creating a new version in the same hidden conversation."));
        var (revised, parsed) = await GenerateFinalAsync(session, storageSettings, "revise",
            session.File.SourcePath, conversation, session.VisualReport, session.RuntimeMetrics.VisualPassMilliseconds,
            log, progress, streamProgress, cancellationToken).ConfigureAwait(false);
        conversation.Add(Message("assistant", revised.Content));
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

    private Task<(OmniTextGenerationResult, ImageAnalysisLiteraryResult)> GenerateFinalAsync(
        ImageAnalysisLiterarySession session, StorageSettings storage, string stage, string imagePath,
        IReadOnlyList<ImageAnalysisHiddenMessage> conversation, string visual, long visualMs,
        Action<string> log, IProgress<ImageAnalysisLiteraryProgress>? progress,
        IProgress<ModelStreamChunk>? streamProgress, CancellationToken token) =>
        OmniResponseRecovery.RunAsync(async attempt =>
        {
            var generated = await GenerateCheckedAsync(session, stage, imagePath, conversation, streamProgress, token,
                raw => SaveResponse(session, storage, stage, conversation, raw, log), log).ConfigureAwait(false);
            log($"Omni final attempt received: stage={stage}; attempt={attempt}; {DescribeGeneration(stage, generated)}");
            var candidate = CloneConversation(conversation).ToList();
            candidate.Add(Message("assistant", generated.Content));
            var parsed = ParseSavedResponse(visual, generated.Content, candidate, visualMs, generated.ElapsedMilliseconds, log);
            return (generated, parsed);
        }, stage, log, progress, token);

    private async Task<OmniTextGenerationResult> GenerateCheckedAsync(
        ImageAnalysisLiterarySession session, string command, string imagePath,
        IReadOnlyList<ImageAnalysisHiddenMessage> conversation, IProgress<ModelStreamChunk>? streamProgress,
        CancellationToken token, Action<string>? responseReceived, Action<string> log)
    {
        if (session.ContextBlocked)
            throw new ImageAnalysisContextExhaustedException("The session requires a new context.");
        try
        {
            var result = await _runtime.GenerateAsync(command, imagePath, conversation, streamProgress,
                token, responseReceived, log).ConfigureAwait(false);
            if (result.MaxContextTokens > 0
                && (long)result.InputTokens + result.GeneratedTokens >= OmniContextBudget.Boundary(result.MaxContextTokens))
            {
                session.ContextBlocked = true;
                log("Omni context reached the session boundary after generation; further requests are blocked.");
            }
            return result;
        }
        catch (ImageAnalysisContextExhaustedException)
        {
            session.ContextBlocked = true;
            throw;
        }
    }

    private async Task EnsureWorkerReadyAsync(
        ImageAnalysisLiterarySession session,
        Action<string> log,
        IProgress<ImageAnalysisLiteraryProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (session.ContextBlocked)
            throw new ImageAnalysisContextExhaustedException("The session requires a new context.");
        if (OmniSessionCompatibility.RequiresNewSession(session, _runtime))
            throw new ImageAnalysisModelChangedException();
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
        // Warming up a replacement must not rewrite the provenance of saved work.
        if (OmniSessionCompatibility.RequiresNewSession(session, _runtime)) return;
        session.BundleId = _runtime.BundleId;
        session.PipelineId = _runtime.PipelineId;
        session.PipelineVersion = _runtime.PipelineVersion;
        session.ContractVersion = ImageAnalysisPipelineIds.ContractVersion;
        session.ModelId = _runtime.ModelId;
        session.ModelRevision = _runtime.ModelRevision;
        session.RuntimeId = _runtime.RuntimeId;
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
