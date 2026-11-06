using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.LivingArcade;

public sealed partial class LivingArcadeGeneration
{
    private async Task PrepareBossRewardAsync(LivingArcadeRunStore store, LivingArcadeGenerationRequest request,
        GenerationCheckpoint checkpoint, Action save, CancellationToken ct)
    {
        var run = RequireRun(store, request.RunId);
        if (run.CurrentLevel != request.CurrentLevel || request.CurrentLevel % 3 != 0
            || run.BossRewards.ContainsKey(request.CurrentLevel)) return;
        Status("Reward");
        checkpoint.RewardContext ??= JsonSerializer.Serialize(new
        {
            sourceLevel = request.CurrentLevel, seed = run.Seed, randomDraw = Guid.NewGuid().ToString("N"),
            hero = LivingArcadeHeroBalance.Normalize(run.EntryState),
            previousRewards = run.BossRewards.OrderBy(pair => pair.Key).Select(pair => new
                { sourceLevel = pair.Key, pair.Value.TitleEn, pair.Value.ShotPattern,
                    pair.Value.DamageMultiplier, pair.Value.FireRateMultiplier, pair.Value.Pierce, pair.Value.DurationLevels }),
            recentResults = run.History.TakeLast(3)
        }, LivingArcadeLevelPolicy.JsonOptions);
        save();
        checkpoint.BossReward ??= await AskAsync(request.Paths, false, BossRewardPrompt,
            checkpoint.RewardContext, 850, ParseBossReward, ct, responseFormat: BossRewardResponseFormat());
        LivingArcadeRewardPolicy.Validate(checkpoint.BossReward);
        save();
        ct.ThrowIfCancellationRequested();
        if (store.TrySaveBossReward(request.RunId, request.CurrentLevel, checkpoint.BossReward)) RunUpdated?.Invoke(request.RunId);
    }

    internal static LivingArcadeBossReward ParseBossReward(string json)
    {
        if (json.Length is < 2 or > 12_000) throw new InvalidDataException("Arcade.Ai.InvalidOutput");
        using var document = JsonDocument.Parse(json, new() { MaxDepth = 4 });
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || document.RootElement.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count()
                != document.RootElement.EnumerateObject().Count())
            throw new InvalidDataException("Arcade.Ai.InvalidOutput");
        var reward = JsonSerializer.Deserialize<LivingArcadeBossReward>(json, LivingArcadeLevelPolicy.JsonOptions)
            ?? throw new InvalidDataException("Arcade.Ai.InvalidOutput");
        // Model-authored content never supplies or changes its own provenance.
        reward.Credits = [new("boss_reward", "Qwen/Qwen2.5-Coder-7B-Instruct-GGUF", LivingArcadeModels.MainRevision)];
        reward.GeneratedUtc = DateTimeOffset.UtcNow;
        LivingArcadeRewardPolicy.Validate(reward);
        return reward;
    }

    internal static object BossRewardResponseFormat() => new
    {
        type = "json_schema", json_schema = new { name = "arcade_boss_reward", strict = true, schema = new
        {
            type = "object", additionalProperties = false,
            properties = new
            {
                titleRu = new { type = "string", minLength = 1, maxLength = 80 },
                titleEn = new { type = "string", minLength = 1, maxLength = 80 },
                descriptionRu = new { type = "string", minLength = 1, maxLength = 500 },
                descriptionEn = new { type = "string", minLength = 1, maxLength = 500 },
                durationLevels = new { type = "integer", minimum = 1, maximum = 2 },
                damageMultiplier = new { type = "number", minimum = 1, maximum = 2 },
                fireRateMultiplier = new { type = "number", minimum = 1, maximum = 1.5 },
                shotPattern = new { type = "string", @enum = new[] { "straight", "fan", "wave", "returning" } },
                pierce = new { type = "integer", minimum = 0, maximum = 2 },
                colorHex = new { type = "string", pattern = "^#[0-9a-fA-F]{6}$" }
            },
            required = new[] { "titleRu", "titleEn", "descriptionRu", "descriptionEn", "durationLevels",
                "damageMultiplier", "fireRateMultiplier", "shotPattern", "pierce", "colorHex" }
        } }
    };

    private const string BossRewardPrompt = """
        You are the arcade's game designer. Invent ONE distinctive, random and significant reward for defeating this stage's boss.
        This is NOT a permanent character upgrade. It will last for exactly 1 or 2 following completed game stages.
        Choose the combination yourself, informed by the random draw, recent rewards and the player's current weapon.
        Do not just copy the previous reward; vary its mechanical identity, title, colour and duration.
        Never repeat the most recent reward's entire combination of pattern, damage, firing, pierce and duration.
        Return only the schema's JSON. The host executes finite primitives, never generated code.
        Damage multiplier 1..2; firing multiplier 1..1.5; durationLevels 1 or 2; pierce 0..2 additional targets.
        shotPattern: straight keeps the weapon's normal path; fan adds 2 projectiles up to 5; wave curves its projectiles;
        returning sends projectiles out then back. Do not claim unsupported effects such as invulnerability, healing,
        helpers, infinite ammo, autoaim, mines, slowed enemies or cooldown changes.
        At least one must be significant: damage >= 1.35 OR firing >= 1.25 OR fan pattern OR pierce > 0.
        Wave and returning patterns alone are not a significant buff. Always combine either one with
        damage >= 1.35, firing >= 1.25 or pierce > 0. The fan adds 2 projectiles to the ordinary weapon, capped at 5.
        Budget = damageMultiplier * fireRateMultiplier * (fan?1.6:1) * (1+0.25*pierce) must be <=4.
        Titles and concise human descriptions must be RU and EN and accurately explain the changed mechanics and duration.
        Write plain, natural Russian with correct grammar and word agreement. Explain the effect to a player,
        without JSON field names or technical jargon. Do not list changes whose multiplier is one or whose amount is zero.
        Use only real supported effects. No markdown, quotes from books, engine internals or author/provenance fields.
        """;
}
