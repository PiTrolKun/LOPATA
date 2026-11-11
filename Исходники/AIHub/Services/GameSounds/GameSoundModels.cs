using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.GameSounds;

internal static class GameSoundModels
{
    private const string BigQwenId = ManagedModelCatalog.Qwen36ExecutorArtifactId;
    private const string BigQwenRepository = ManagedModelCatalog.Qwen36ExecutorRepository;
    private const string BigQwenRevision = "b3af3a64a15eb981da65f9074f81f1cc0804be75";
    private const string BigQwenFile = "qwen3.6-27b-q4_k_m.gguf";
    private const string BigQwenHash = "2ac61afbc9c600899ab57fe6333116179b9b08a6230c9bcaca5afd62ad75b7d7";

    internal static ManagedModelArtifactCard[] Available(StorageSettings settings, ManagedModelLibraryStore library)
    {
        var inventory = library.LoadAll();
        var predefined = ManagedModelCatalog.CreatePredefined(settings);
        var core = predefined.First(c => c.ModelArtifactId == ManagedModelCatalog.CoreArtifactId);
        var gemma = predefined.First(c => c.ModelArtifactId == ManagedModelCatalog.LetItOutArtifactId);
        var root = core.ModelsRoot;
        var qwen = new ManagedModelArtifactCard
        {
            ModelArtifactId = BigQwenId, Family = "Qwen3.6-27B", DisplayName = BigQwenRepository,
            Role = ManagedModelRoles.Executor, Provider = "Hugging Face", RepositoryId = BigQwenRepository,
            Revision = BigQwenRevision, Format = "GGUF", Architecture = "qwen35", Quantization = "Q4_K_M",
            ParameterCount = 27_000_000_000, License = "Apache-2.0", SourcePage = $"https://huggingface.co/{BigQwenRepository}",
            IsManaged = true, CanRemoveFiles = true, ModelsRoot = root,
            InstallDirectory = string.IsNullOrEmpty(root) ? "" : Path.Combine(root, "Executors", BigQwenRepository.Replace('/', '_')),
            Origin = ManagedModelOrigins.PredefinedScenario, RuntimeBackend = LlamaBackendPaths.DisplayName,
            Files = [new()
            {
                RelativePath = BigQwenFile, SizeBytes = 16_547_398_976, Sha256 = BigQwenHash,
                SourceUrl = $"https://huggingface.co/{BigQwenRepository}/resolve/{BigQwenRevision}/{BigQwenFile}",
                Purpose = "executor_model", IsRequired = true
            }]
        };
        return [Existing(core), Existing(qwen), Existing(gemma)];

        ManagedModelArtifactCard Existing(ManagedModelArtifactCard card)
        {
            var found = inventory.FirstOrDefault(c => c.ModelArtifactId == card.ModelArtifactId && c.IsManaged);
            if (found is null) return card;
            if (card.ModelArtifactId != BigQwenId) return found;
            var weight = found.Files.FirstOrDefault(f => f.Purpose == "executor_model" &&
                f.RelativePath == BigQwenFile && f.Sha256.Equals(BigQwenHash, StringComparison.OrdinalIgnoreCase));
            if (weight is null) return card;
            found.Revision = BigQwenRevision;
            return found;
        }
    }

    internal static DebugModelInfo Model(ManagedModelArtifactCard card)
    {
        var file = card.Files.First(f => f.Purpose is "main_model" or "executor_model");
        return new() { Name = card.DisplayName, Format = "gguf", Path = Path.Combine(card.InstallDirectory, file.RelativePath),
            SizeBytes = file.SizeBytes, Role = "game_sound_designer" };
    }
}

internal sealed record GameSoundAnswer(string Text, GameSoundRecipe? Recipe);
internal sealed record GameSoundPointer(string ProjectId, string RequestId);

internal sealed class GameSoundModelSession : IDisposable
{
    internal const string Kind = "game-sounds";
    private readonly LlamaServerRuntimeService _runtime = new(new UserContextService(new UserProfileStore(), new IpLocationService()));
    private (string Id, GameSoundPlacement Placement)? _prepared;

    internal GameSoundModelSession() => _runtime.ConfigurePrivateNoRetry();
    internal LlamaServerRuntimeService.SessionInfo? Session => _runtime.PoetrySession;

    internal async Task WarmAsync(ManagedModelArtifactCard card, GameSoundPlacement placement, CancellationToken token)
    {
        if (_prepared == (card.ModelArtifactId, placement) && Session is not null) return;
        var required = placement switch
        {
            GameSoundPlacement.Vram => LlamaPlacementMode.Gpu,
            GameSoundPlacement.Hybrid => LlamaPlacementMode.Hybrid,
            GameSoundPlacement.Cpu => LlamaPlacementMode.Cpu,
            _ => (LlamaPlacementMode?)null
        };
        await ApplicationBackgroundOperations.RetireModelsExceptAsync(_runtime);
        await _runtime.PreparePoetryAsync(GameSoundModels.Model(card), token,
            reasoningOptions: new MusicPoetryReasoningOptions(true, 2048), startupTimeout: TimeSpan.FromMinutes(3),
            adaptivePlacement: true, requiredPlacement: required);
        _prepared = (card.ModelArtifactId, placement);
    }

    internal async Task RetireAsync()
    {
        _prepared = null;
        await _runtime.RetirePoetryAsync();
    }

    internal Task ReleaseOtherModelsAsync() => ApplicationBackgroundOperations.RetireModelsExceptAsync(_runtime);

    internal async Task<GameSoundAnswer> AskAsync(ManagedModelArtifactCard card, GameSoundModelOptions options,
        GameSoundRequest request, IReadOnlyList<GameSoundMessage> history, IReadOnlyList<GameSoundSource> sources,
        Action<MusicPoetryStreamUpdate> progress, Action<string> log, CancellationToken token)
    {
        await WarmAsync(card, options.Placement, token);
        var model = GameSoundModels.Model(card);
        var capacity = await _runtime.LetItOutEffectiveCapacityAsync(token);
        var turns = DiscussionTurns(history);
        var layerData = sources.Where(s => s.Enabled).Select(s => new { s.Recipe.Name, s.StartMs, s.Level, s.Recipe.Sound }).ToArray();
        var language = request.Language.StartsWith("ru", StringComparison.OrdinalIgnoreCase) ? "Russian" : "English";
        var system = request.Generate
            ? $"You design exactly one short procedural game sound. Output one JSON object only, with a short {language} name when needed. " +
                "Use attackMs + releaseMs <= durationMs. Frequency is in Hz; noise, brightness and level are percentages. " +
                "Retro tones use square or triangle, with a sweep for jumps and a downward sweep for lasers. For hits or explosions use a low falling tone with noise and release. " +
                "Use layers only as context. When creating the first sound, use the starting settings as a reference and return a full new recipe. " +
                "For an edit, return only changed fields. Preserve all unrelated values. Never emit code, commentary, paths or multiple objects."
            : $"You are a practical sound design assistant. Answer in {language}. Discuss short game sound effects and explain suggestions in plain language. " +
                "The user is in Discussion mode. Discuss and explain; do not claim that any recipe or audio file was created.";
        var correction = "";
        var thinking = !request.Generate || options.GenerationThinking;
        var historyStart = 0;
        var layersStart = 0;
        for (var attempt = 0; ; attempt++)
        {
            var target = request.Target;
            string Payload(int outputTokens)
            {
                var messages = new List<object> { new { role = "system", content = system } };
                if (!request.Generate)
                    messages.AddRange(turns.Skip(historyStart).Select(h => (object)new
                    { role = h.Kind == "user" ? "user" : "assistant", content = h.Text }));
                var input = request.Text + correction;
                if (request.Generate)
                {
                    if (target is not null) input += "\nCurrent recipe: " + JsonSerializer.Serialize(target.Sound, GameSoundProjects.Json);
                    else if (!request.Orchestrate) input += "\nStarting settings: " + JsonSerializer.Serialize(new GameSoundParameters(), GameSoundProjects.Json);
                    if (request.Orchestrate)
                        input += "\nSelected layers, from oldest to newest: " +
                            JsonSerializer.Serialize(layerData.Skip(layersStart), GameSoundProjects.Json);
                }
                messages.Add(new { role = "user", content = input });
                var payload = new Dictionary<string, object>
                {
                    ["messages"] = messages, ["stream"] = true, ["max_tokens"] = outputTokens,
                    ["temperature"] = .15 + options.Variety * .0085, ["top_p"] = .9,
                    ["chat_template_kwargs"] = new { enable_thinking = thinking }
                };
                if (request.Generate) payload["response_format"] = GameSoundRecipes.Schema(target is not null);
                return JsonSerializer.Serialize(payload);
            }

            var reserve = request.Generate ? thinking ? 2800 : 720 : 256;
            int inputTokens;
            while (true)
            {
                inputTokens = await _runtime.CountPrivatePromptAsync(Payload(reserve), token);
                if ((long)inputTokens + LiteraryAutomaticBudget.SafetyTokens + reserve <= capacity) break;
                if (!request.Generate && historyStart < turns.Length)
                    historyStart += 2 * Math.Max(1, (turns.Length - historyStart) / 6);
                else if (request.Generate && layersStart < layerData.Length)
                    layersStart += Math.Max(1, (layerData.Length - layersStart) / 3);
                else throw new InvalidDataException("GameSounds.ContextFull");
            }
            var output = LiteraryAutomaticBudget.Reply(capacity, inputTokens);
            if (request.Generate) output = Math.Min(reserve, output);
            var result = await _runtime.GeneratePoetryStreamAsync(model, Payload(output), progress, token);
            token.ThrowIfCancellationRequested();
            if (!request.Generate)
            {
                if (string.IsNullOrWhiteSpace(result.Content)) throw new InvalidDataException("GameSounds.EmptyReply");
                return new(result.Content.Trim(), null);
            }
            try
            {
                if (result.FinishReason != "stop") throw new InvalidDataException("Return one complete JSON object.");
                var (name, sound) = GameSoundRecipes.Parse(result.Content, target);
                OutputModelUse use = new(card.ModelArtifactId, card.DisplayName, card.Revision,
                    target is null ? "game-sound/recipe" : "game-sound/edit");
                var recipe = target is null ? GameSoundRecipe.Create(name, sound, [use]) : target with
                { Name = name, Sound = sound, Models = target.Models.Append(use).Distinct().ToArray() };
                return new(recipe.Name, recipe);
            }
            catch (Exception error) when (error is JsonException or InvalidDataException)
            {
                log(error.Message);
                if (attempt > 0) throw new InvalidDataException("GameSounds.InvalidModelReply", error);
                correction = "\nThe last result failed validation. Return only a corrected recipe object. " + error.Message;
            }
        }
    }

    private static GameSoundMessage[] DiscussionTurns(IReadOnlyList<GameSoundMessage> messages)
    {
        var turns = new List<GameSoundMessage>();
        GameSoundMessage? request = null;
        foreach (var message in messages)
        {
            if (message.Kind == "user") { request = message; continue; }
            if (request is null || message.Kind is not ("answer" or "made" or "updated")) continue;
            turns.Add(request);
            turns.Add(message.Kind == "answer" ? message : new("answer", $"Sound recipe: {message.Text}"));
            request = null;
        }
        return turns.ToArray();
    }

    public void Dispose() => _runtime.Dispose();
}
