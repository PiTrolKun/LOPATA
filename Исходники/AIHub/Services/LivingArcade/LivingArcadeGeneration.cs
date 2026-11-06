using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.LivingArcade;

public sealed record LivingArcadeWarmupRequest(LivingArcadeModelPaths Paths, string Language);
public sealed record LivingArcadeFirstEventsRequest(string RunId, LivingArcadeModelPaths Paths, string Language);
public sealed record LivingArcadeGenerationRequest(string RunId, int CurrentLevel, LivingArcadeModelPaths Paths, string Language);
internal sealed record LivingArcadeInferenceMetric(string ModelId, string Stage, double Seconds,
    int? PromptTokens, int? CompletionTokens);
internal sealed record LivingArcadeInferenceTrace(string ModelId, string Stage, int Attempt,
    string RequestJson, string ResponseJson);

/// <summary>Four logical jobs, two pinned models, one owned runtime. Only validated data reaches the game.</summary>
public sealed partial class LivingArcadeGeneration : IDisposable
{
    public const string WarmupKind = "game.living_arcade.warmup";
    public const string FirstEventsKind = "game.living_arcade.firstevents";
    public const string Kind = "game.living_arcade.generate";
    private readonly LlamaServerRuntimeService _runtime;
    private string? _preparedPath;
    private string _logicalStage = "Arcade.Ai.Warming";
    private bool _disposed;
    public event Action<string>? StatusChanged;
    public event Action<string>? RunUpdated;
    internal event Action<LivingArcadeInferenceMetric>? InferenceCompleted;
    // Opt-in test diagnostics only: the product never subscribes or writes private machine inputs to disk.
    internal event Action<LivingArcadeInferenceTrace>? InferenceTrace;
    internal LlamaServerRuntimeService.SessionInfo? RuntimeSession => _runtime.PoetrySession;

    public LivingArcadeGeneration(UserContextService userContext)
    {
        _runtime = new(userContext);
        _runtime.ConfigurePrivateNoRetry();
    }

    public Task WarmAsync(LivingArcadeModelPaths paths, string language, CancellationToken token,
        BackgroundOperationState? restored = null) => ApplicationBackgroundOperations.RunAsync(WarmupKind,
        Title(language, "Warming"), null, new LivingArcadeWarmupRequest(paths, language), async ct =>
        {
            try
            {
                Status("Warming");
                await LivingArcadeModels.VerifyForUseAsync(paths, ct);
                await PrepareModelAsync(paths, true, ct);
                ApplicationBackgroundOperations.Current?.SaveCheckpoint(new { ArtistWarmed = true });
                await PrepareModelAsync(paths, false, ct);
                Status("Ready"); return true;
            }
            catch { await RetireAsync(); throw; }
        }, token, restored);

    public Task PrepareFirstEventsAsync(LivingArcadeRunStore store, string runId, LivingArcadeModelPaths paths,
        string language, CancellationToken token, BackgroundOperationState? restored = null)
    {
        List<LivingArcadeMiniEvent>? confirmedEvents = null;
        LivingArcadeChapterScenery? confirmedScenery = null;
        return ApplicationBackgroundOperations.RunAsync(FirstEventsKind, Title(language, "FirstEvents"), runId,
        new LivingArcadeFirstEventsRequest(runId, paths, language), async ct =>
        {
            try
            {
                var run = RequireRun(store, runId);
                if (run.FirstEventsPrepared || run.CurrentLevel != 1) return true;
                await LivingArcadeModels.VerifyForUseAsync(paths, ct);
                var first = run.Levels[1];
                var events = confirmedEvents;
                if (events is null && restored?.Checkpoint is { } saved)
                {
                    var receipt = saved.Deserialize<FirstEventsCheckpoint>() ?? throw new InvalidDataException("Arcade.Ai.InvalidCheckpoint");
                    if (receipt.RunId != runId) throw new InvalidDataException("Arcade.Ai.InvalidCheckpoint");
                    if (receipt.Events is not null)
                        events = ParseFirstEvents(JsonSerializer.Serialize(new { events = receipt.Events }, LivingArcadeLevelPolicy.JsonOptions), first);
                    confirmedScenery = receipt.Scenery;
                }
                confirmedScenery = await PrepareOptionalSceneryAsync(store, runId, 1, paths, confirmedScenery, scenery =>
                {
                    confirmedScenery = scenery;
                    ApplicationBackgroundOperations.Current?.SaveCheckpoint(new FirstEventsCheckpoint(runId, events, scenery));
                }, ct);
                Status("FirstEvents");
                events ??= await AskAsync(paths, true, MiniEventsPrompt, JsonSerializer.Serialize(new {
                    durationSeconds = first.DurationSeconds, seed = run.Seed,
                    enemyIds = first.EnemyTypes.Select(e => e.Id), existingEvents = first.MiniEvents
                }, LivingArcadeLevelPolicy.JsonOptions), 700, json => ParseFirstEvents(json, first), ct,
                    responseFormat: FirstEventsResponseFormat(first));
                confirmedEvents = events;
                ApplicationBackgroundOperations.Current?.SaveCheckpoint(new FirstEventsCheckpoint(runId, events, confirmedScenery));
                ct.ThrowIfCancellationRequested();
                store.TrySaveFirstMiniEvents(runId, events, [new("first-level mini-events",
                    "Qwen/Qwen2.5-Coder-3B-Instruct-GGUF", LivingArcadeModels.ArtistRevision)]);
                RunUpdated?.Invoke(runId); Status("Ready"); return true;
            }
            catch { await RetireAsync(); throw; }
        }, token, restored);
    }

    public Task PrepareAheadAsync(LivingArcadeRunStore store, string runId, LivingArcadeModelPaths paths,
        string language, CancellationToken token, BackgroundOperationState? restored = null)
    {
        var current = RequireRun(store, runId).CurrentLevel;
        if (restored is not null)
        {
            var input = restored.Input.Deserialize<LivingArcadeGenerationRequest>()
                ?? throw new InvalidDataException("Arcade.Ai.InvalidCheckpoint");
            if (input.RunId != runId || input.Paths != paths) throw new InvalidDataException("Arcade.Ai.InvalidCheckpoint");
            current = input.CurrentLevel;
        }
        var request = new LivingArcadeGenerationRequest(runId, current, paths, language);
        // The controller may invoke this same delegate again after pause. Keep confirmed stages across that restart.
        var checkpoint = ReadCheckpoint(restored, request);
        return ApplicationBackgroundOperations.RunAsync(Kind, Title(language, "Preparing"), runId, request,
            ct => GenerateAheadAsync(store, request, checkpoint, ct), token, restored);
    }

    private async Task<bool> GenerateAheadAsync(LivingArcadeRunStore store, LivingArcadeGenerationRequest request,
        GenerationCheckpoint checkpoint, CancellationToken ct)
    {
        void Save() => ApplicationBackgroundOperations.Current?.SaveCheckpoint(checkpoint);
        try
        {
            var run = RequireRun(store, request.RunId);
            if (run.CurrentLevel != request.CurrentLevel) return true; // A completed stage was already consumed.
            await LivingArcadeModels.VerifyForUseAsync(request.Paths, ct);
            var currentChapter = LivingArcadeSceneryArtPolicy.ChapterFor(request.CurrentLevel);
            checkpoint.SceneryChapters.TryGetValue(currentChapter, out var currentScenery);
            await PrepareOptionalSceneryAsync(store, request.RunId, request.CurrentLevel, request.Paths, currentScenery,
                scenery => { checkpoint.SceneryChapters[currentChapter] = scenery; Save(); }, ct);
            await PrepareOptionalBossArtAsync(store, request, request.CurrentLevel, checkpoint, Save, ct);
            await PrepareBossRewardAsync(store, request, checkpoint, Save, ct);
            run = RequireRun(store, request.RunId);
            if (run.CurrentLevel != request.CurrentLevel) return true;
            var target = request.CurrentLevel + 1;
            if (!run.Levels.ContainsKey(target))
            {
                checkpoint.InputContext ??= Context(run, target);
                Save();
                var context = checkpoint.InputContext;
                if (checkpoint.Design is null || ReadTypedPlan(checkpoint.Design) is null)
                {
                    Status("Designing");
                    checkpoint.Design = await AskAsync(request.Paths, false, DesignerPrompt,
                        context, 1000, json => ParseTypedDraft(json, target), ct,
                        responseFormat: DesignerResponseFormat(target, AllowsLargeEncounter(run.Seed,target)));
                    checkpoint.Candidate = null; checkpoint.Probe = null; checkpoint.Level = null; checkpoint.Art = null;
                    checkpoint.ProgramAttempt = 0; checkpoint.Rejection = null; checkpoint.ReviewReason = null; checkpoint.Attempts = [];
                    Save();
                }
                if (checkpoint.Level is null)
                {
                    var plan = ReadTypedPlan(checkpoint.Design) ?? throw new InvalidDataException("Arcade.Ai.InvalidCheckpoint");
                    if(plan.RareLarge && !AllowsLargeEncounter(run.Seed,target)) throw new InvalidDataException("Arcade.Ai.InvalidCheckpoint");
                    var instruction = context + "\nConcept for flavor only: " + checkpoint.Design.Concept
                        + "\nAUTHORITATIVE EXECUTABLE PLAN: " + JsonSerializer.Serialize(plan, CompactPlanOptions);
                    for (; checkpoint.ProgramAttempt <= 2;)
                    {
                        LivingArcadeLevel level;
                        if (checkpoint.Candidate is { } candidate)
                        { ValidatePlanImplementation(candidate, plan); level = candidate; }
                        else try
                        {
                            Status("Programming");
                            level = await AskAsync(request.Paths, false, ProgrammerPrompt,
                                instruction + (checkpoint.Rejection is null ? "" : "\nCorrect the previous candidate: " + checkpoint.Rejection),
                                6500, json => { var generated = ParseProgrammedLevel(json, target, run.Seed, run.EntryState);
                                    ValidatePlanImplementation(generated, plan); return generated; }, ct, repairs: 0,
                                responseFormat: LevelResponseFormat(target, plan));
                            checkpoint.Candidate = level; Save();
                        }
                        catch (Exception error) when (error is JsonException or InvalidDataException
                            && error.Message != "Arcade.Ai.ContextTooSmall")
                        {
                            var detail = error.Data["ValidationDetail"] as string ?? error.Message;
                            checkpoint.Rejection = "JSON schema or allowed range violation: " + detail[..Math.Min(detail.Length, 900)];
                            checkpoint.ProgramAttempt++; Save();
                            if (checkpoint.ProgramAttempt > 2) throw new BackgroundOperationFailedException("Arcade.Ai.Rejected", error);
                            continue;
                        }
                        Status("Testing");
                        checkpoint.Probe ??= await Task.Run(() => ProbeBaselineCandidate(level,
                            run.EntryState, ct), ct);
                        Save();
                        if (!checkpoint.Probe.Accepted)
                        {
                            checkpoint.Attempts.Add(new(level, checkpoint.Probe, false, checkpoint.Probe.RejectionKey ?? "cost"));
                            checkpoint.Rejection = "Bounded simulation/cost probe rejected the level: " + checkpoint.Probe.RejectionKey
                                + ". Limit expanded spawns1200, simultaneous enemies80, conservative 12-second hostile shot envelope480. "
                                + $"Your peak enemy/shot estimates: {checkpoint.Probe.PeakScheduledEnemies}/{checkpoint.Probe.EstimatedPeakHostileShots:0}. "
                                + "Reduce simultaneous shooters, volley size, shot frequency or split cascades.";
                            checkpoint.Candidate = null; checkpoint.Probe = null; checkpoint.ProgramAttempt++; Save(); continue;
                        }
                        Status("Reviewing");
                        var verdict = await AskAsync(request.Paths, false, CriticPrompt,
                            ReviewInput(run, checkpoint.Design, level, checkpoint.Probe, PlayableCandidate(level)),
                            850, json => ParseVerdict(json, level, exactPlanVerified: true), ct, responseFormat: CriticResponseFormat(level));
                        checkpoint.ReviewReason = verdict.Reason;
                        checkpoint.Attempts.Add(new(level, checkpoint.Probe, verdict.Approved, verdict.Reason));
                        if (verdict.Approved) { checkpoint.Level = level; Save(); break; }
                        checkpoint.Rejection = verdict.Reason; checkpoint.Candidate = null;
                        checkpoint.Probe = null;
                        checkpoint.ProgramAttempt++; Save();
                    }
                    if (checkpoint.Level is null) throw new BackgroundOperationFailedException("Arcade.Ai.Rejected");
                }
                ValidatePlanImplementation(checkpoint.Level,
                    ReadTypedPlan(checkpoint.Design) ?? throw new InvalidDataException("Arcade.Ai.InvalidCheckpoint"));
                if (checkpoint.Art is null)
                {
                    Status("Drawing");
                    var library = LivingArcadeLibrary.Load();
                    checkpoint.Art = await AskAsync(request.Paths, true, ArtistPrompt,
                        ArtInput(checkpoint.Level,checkpoint.Design,library), 6500,
                        json => ParsePreparedArt(json, checkpoint.Level,library), ct,
                        responseFormat: ArtistResponseFormat(checkpoint.Level,library));
                    Save();
                }
                ApplyArt(checkpoint.Level, checkpoint.Art);
                var targetChapter = LivingArcadeSceneryArtPolicy.ChapterFor(target);
                checkpoint.SceneryChapters.TryGetValue(targetChapter, out var targetScenery);
                var preparedScenery = await PrepareOptionalSceneryAsync(store, request.RunId, target, request.Paths, targetScenery,
                    scenery => { checkpoint.SceneryChapters[targetChapter] = scenery; Save(); }, ct);
                checkpoint.Level.Scenery = LivingArcadeSceneryArtPolicy.Copy(preparedScenery.Artwork);
                RecordArtSources(checkpoint.Level,LivingArcadeLibrary.Load());
                checkpoint.Level.Credits = Credits();
                LivingArcadeLevelPolicy.Validate(checkpoint.Level);
                ct.ThrowIfCancellationRequested();
                if (store.TrySaveLevel(request.RunId, checkpoint.Level)) RunUpdated?.Invoke(request.RunId);
            }
            run = RequireRun(store, request.RunId);
            if (run.Levels.ContainsKey(target))
            {
                var preparedChapter = LivingArcadeSceneryArtPolicy.ChapterFor(target);
                checkpoint.SceneryChapters.TryGetValue(preparedChapter, out var existingScenery);
                await PrepareOptionalSceneryAsync(store, request.RunId, target, request.Paths, existingScenery,
                    scenery => { checkpoint.SceneryChapters[preparedChapter] = scenery; Save(); }, ct);
                await PrepareOptionalBossArtAsync(store, request, target, checkpoint, Save, ct);
                run = RequireRun(store, request.RunId);
            }
            if (run.CurrentLevel == request.CurrentLevel && run.Draft?.Number != target + 1 && !run.Levels.ContainsKey(target + 1))
            {
                if (checkpoint.Draft is null)
                {
                    Status("Drafting");
                    checkpoint.DraftContext ??= Context(run, target + 1); Save();
                    checkpoint.Draft = await AskAsync(request.Paths, false, DesignerPrompt,
                        checkpoint.DraftContext, 1000, json => ParseTypedDraft(json, target + 1), ct,
                        responseFormat: DesignerResponseFormat(target + 1, AllowsLargeEncounter(run.Seed,target+1)));
                    Save();
                }
                ct.ThrowIfCancellationRequested();
                if (store.TrySaveDraft(request.RunId, checkpoint.Draft)) RunUpdated?.Invoke(request.RunId);
            }
            Status("Ready"); return true;
        }
        catch { await RetireAsync(); throw; }
    }

    private static LivingArcadeRun RequireRun(LivingArcadeRunStore store, string id) =>
        store.Load(id) ?? throw new InvalidDataException("LivingArcade.DeletedRun");
    private void Status(string stage)
    {
        var key = "Arcade.Ai." + stage;
        if (stage != "Loading") _logicalStage = key;
        StatusChanged?.Invoke(key);
    }
    private static string Title(string language, string stage)
    { var localize = new LocalizationService(); localize.Load(language); return localize.T("Arcade.Ai." + stage); }
    private static string Context(LivingArcadeRun run, int number) => JsonSerializer.Serialize(new {
        targetNumber = number, seed = run.Seed, currentNumber = run.CurrentLevel,
        largeEncounterAllowed = AllowsLargeEncounter(run.Seed,number),
        supportedGenerationProfile = GenerationCapabilities,
        hero = ModelBaselineHero(run.EntryState), attempts = run.Attempts,
        balanceProfile = BalanceInstruction,
        hostProgression = new { characterLevel = LivingArcadeHeroBalance.Progress(run.EntryState.TotalKills).Level,
            damageGainPerCharacterLevel = .025, strongPowerupsSeconds = 8, shieldSeconds = 6,
            experiencePerOrdinaryEnemy = 1, experiencePerActiveShooter = 5, experiencePerBoss = 30,
            activeShooterHealthMultiplier = .9,
            generatedEnemyHealthConstraint = "Only combat HP limit: an approaching non-final-boss enemy must die under focused frontal connected fire from visible appearance before contact. Runtime uses the conservative minimum across ordinary weapons and temporary laser/rockets, weak/jam, shield openings and any supported time-mode switches; positive buffs never inflate the budget. Raw model health stays unchanged; actual spawn health may only be reduced. There are no role HP bands.",
            ordinaryWeapons = new[] { "pistol", "shotgun", "smg" }, temporaryWeapons = new[] { "laser", "rockets" },
            rareCompanions = "The host can supply rare one-life helpers with their own health.",
            bossRewardDurationCompletedStages = "1 or 2; retained on death and retry, separately designed by the host's model call",
            bossAfterOrdinaryWaves = number % 3 == 0, bossPhases = 3,
            note = "Host supplies typed pickups, progressive tempo and the final boss. Design ordinary waves; do not duplicate the final boss." },
        history = run.History.TakeLast(6).Select(h => new { h.Level, h.Seconds, h.DamageTaken, h.BonusCount, h.Attempts }),
            previous = run.Levels.Values.OrderBy(l => l.Number).TakeLast(3).Select(l => new {
            l.Number, l.TitleEn, enemyBehaviors = l.EnemyTypes.Take(6).Select(e => new {
                e.Id, motions = e.States.Select(s => s.Motion).Distinct(), shield = e.States.Any(s => s.Shield),
                orderedMotionCycle = CycleSignature(e), firing = e.States.Any(s => s.ShotInterval > 0),
                volley = e.States.Any(s => s.ShotInterval > 0 && s.ShotCount > 1), e.DeathSpawnId, e.DeathSpawnCount }) }),
        existingDraft = run.Draft?.Number == number ? run.Draft : null
    }, LivingArcadeLevelPolicy.JsonOptions);

    private sealed class GenerationCheckpoint
    {
        public string RunId { get; set; } = "";
        public int CurrentLevel { get; set; }
        public int BalanceProfile { get; set; }
        public string? InputContext { get; set; }
        public string? DraftContext { get; set; }
        public LivingArcadeDraft? Design { get; set; }
        public LivingArcadeLevel? Candidate { get; set; }
        public LivingArcadeProbeResult? Probe { get; set; }
        public string? ReviewReason { get; set; }
        public int ProgramAttempt { get; set; }
        public string? Rejection { get; set; }
        public List<GenerationAttemptReceipt> Attempts { get; set; } = [];
        public LivingArcadeLevel? Level { get; set; }
        public ArtPatch? Art { get; set; }
        public LivingArcadeDraft? Draft { get; set; }
        public LivingArcadeBossReward? BossReward { get; set; }
        public string? RewardContext { get; set; }
        public Dictionary<int, LivingArcadeChapterScenery> SceneryChapters { get; set; } = [];
        public Dictionary<int, LivingArcadeBossArtReceipt> BossArtLevels { get; set; } = [];
    }
    private sealed record GenerationAttemptReceipt(LivingArcadeLevel Candidate, LivingArcadeProbeResult Probe, bool Approved, string Reason);
    private sealed record FirstEventsCheckpoint(string RunId, List<LivingArcadeMiniEvent>? Events = null,
        LivingArcadeChapterScenery? Scenery = null);
    private static GenerationCheckpoint ReadCheckpoint(BackgroundOperationState? restored, LivingArcadeGenerationRequest request)
    {
        if (restored?.Checkpoint is not { } json) return new() { RunId = request.RunId,
            CurrentLevel = request.CurrentLevel, BalanceProfile = BalanceProfileVersion };
        if (json.GetRawText().Length > 3_000_000) throw new InvalidDataException("Arcade.Ai.InvalidCheckpoint");
        var value = json.Deserialize<GenerationCheckpoint>() ?? throw new InvalidDataException("Arcade.Ai.InvalidCheckpoint");
        if (value.RunId != request.RunId || value.CurrentLevel != request.CurrentLevel || value.ProgramAttempt is < 0 or > 3
            || value.Rejection?.Length > 1000 || value.InputContext?.Length > 60_000 || value.DraftContext?.Length > 60_000
            || value.Attempts is null || value.Attempts.Count > 3 || value.RewardContext?.Length > 60_000
            || value.BalanceProfile is < 0 or > BalanceProfileVersion)
            throw new InvalidDataException("Arcade.Ai.InvalidCheckpoint");
        if (value.SceneryChapters is null || value.SceneryChapters.Count > 2)
            throw new InvalidDataException("Arcade.Ai.InvalidCheckpoint");
        foreach (var (chapter, scenery) in value.SceneryChapters)
        {
            if (scenery is null || chapter < 0 || chapter != scenery.Chapter)
                throw new InvalidDataException("Arcade.Ai.InvalidCheckpoint");
            scenery.Validate(scenery.RunSeed, chapter);
        }
        if (value.BossArtLevels is null || value.BossArtLevels.Count > 2)
            throw new InvalidDataException("Arcade.Ai.InvalidCheckpoint");
        foreach (var (number, artwork) in value.BossArtLevels)
        {
            if (artwork is null || number != request.CurrentLevel && number != request.CurrentLevel + 1)
                throw new InvalidDataException("Arcade.Ai.InvalidCheckpoint");
            artwork.Validate(artwork.RunSeed, number);
        }
        if (value.BossReward is not null)
        {
            if (request.CurrentLevel % 3 != 0) throw new InvalidDataException("Arcade.Ai.InvalidCheckpoint");
            LivingArcadeRewardPolicy.Validate(value.BossReward);
        }
        if (value.Design is not null) ValidateDraft(value.Design, request.CurrentLevel + 1);
        if (value.Level is not null)
        {
            LivingArcadeLevelPolicy.Validate(value.Level);
            if (value.Level.Number != request.CurrentLevel + 1) throw new InvalidDataException("Arcade.Ai.InvalidCheckpoint");
        }
        if (value.Candidate is not null)
        {
            LivingArcadeLevelPolicy.Validate(value.Candidate);
            if (value.Candidate.Number != request.CurrentLevel + 1) throw new InvalidDataException("Arcade.Ai.InvalidCheckpoint");
        }
        if (value.Art is not null)
        {
            if (value.Level is null) throw new InvalidDataException("Arcade.Ai.InvalidCheckpoint");
            _ = ParseArt(JsonSerializer.Serialize(value.Art, LivingArcadeLevelPolicy.JsonOptions), value.Level);
        }
        if (value.Draft is not null) ValidateDraft(value.Draft, request.CurrentLevel + 2);
        ResetLegacyBalanceCheckpoint(value);
        return value;
    }
    private static List<LivingArcadeModelCredit> Credits() => [
        new("designer", "Qwen/Qwen2.5-Coder-7B-Instruct-GGUF", LivingArcadeModels.MainRevision),
        new("programmer", "Qwen/Qwen2.5-Coder-7B-Instruct-GGUF", LivingArcadeModels.MainRevision),
        new("critic", "Qwen/Qwen2.5-Coder-7B-Instruct-GGUF", LivingArcadeModels.MainRevision),
        new("artist", "Qwen/Qwen2.5-Coder-3B-Instruct-GGUF", LivingArcadeModels.ArtistRevision) ];

    public async Task RetireAsync() { _preparedPath = null; await _runtime.RetirePoetryAsync(); }
    public void Dispose() { if (_disposed) return; _disposed = true; _runtime.Dispose(); }
}
