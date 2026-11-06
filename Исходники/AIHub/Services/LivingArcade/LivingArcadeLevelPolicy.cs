using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHub.Models;

namespace AIHub.Services.LivingArcade;

/// <summary>Finite declarative mechanics: no generated code, reflection, file or network access.</summary>
public static partial class LivingArcadeLevelPolicy
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
        WriteIndented = true
    };

    public static LivingArcadeLevel Parse(string json)
    {
        // Saved levels may include chapter art and previously accepted enemy drawings. Model transport stays200k.
        if (json.Length is 0 or > 10_000_000) throw Invalid();
        using var document = JsonDocument.Parse(json, new() { MaxDepth = 32 });
        RejectDuplicateProperties(document.RootElement);
        var level = JsonSerializer.Deserialize<LivingArcadeLevel>(json, JsonOptions) ?? throw Invalid();
        Validate(level);
        return level;
    }

    public static void Validate(LivingArcadeLevel level)
    {
        LivingArcadeSceneryArtPolicy.Validate(level.Scenery);
        LivingArcadeBossArtPolicy.Validate(level.BossArt);
        if (level.BossArt is not null && level.Number % 3 != 0) throw Invalid();
        if (level.SchemaVersion != 1 || level.Number < 1 || !Range(level.DurationSeconds, 30, 300)
            || !Range(level.CarryFraction, 0, 1) || !Text(level.TitleRu, 100) || !Text(level.TitleEn, 100)
            || level.DescriptionRu is null || level.DescriptionEn is null
            || level.DescriptionRu.Length > 2000 || level.DescriptionEn.Length > 2000
            || !Color(level.BackgroundColor) || level.EnemyTypes is null || level.EnemyTypes.Count is < 1 or > 12
            || level.Waves is null || level.Waves.Count is < 1 or > 120 || level.MiniEvents is null
            || level.MiniEvents.Count > 32 || level.MusicNotes is null || level.MusicNotes.Count is < 1 or > 64
            || level.MusicNotes.Any(n => n is < 36 or > 96) || level.Mechanics is null
            || level.Mechanics.Count > 20 || level.Mechanics.Any(x => !Text(x, 180))) throw Invalid();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var enemy in level.EnemyTypes)
        {
            if (enemy is null || !Id(enemy.Id) || !ids.Add(enemy.Id) || !Text(enemy.TitleRu, 100)
                || !Text(enemy.TitleEn, 100) || !Range(enemy.Health, .25, 200)
                || !Range(enemy.Speed, .015, .2)
                || enemy.EncounterClass is not ("normal" or "rare-large")
                || !Range(enemy.Radius, .012, enemy.EncounterClass == "rare-large" ? .20 : .07)
                || !Color(enemy.Color) || enemy.Shape is not ("circle" or "square" or "diamond" or "triangle" or "star")
                || enemy.States is null || enemy.States.Count is < 1 or > 8
                || enemy.DeathSpawnCount is < 0 or > 3) throw Invalid();
            LivingArcadeVisualPolicy.Validate(enemy.Visual);
            var states = new HashSet<string>(StringComparer.Ordinal);
            foreach (var state in enemy.States)
            {
                if (state is null || !Id(state.Id) || !states.Add(state.Id) || !Range(state.Duration, .2, 20)
                    || !Range(state.ForwardSpeed, 0, 3) || !Range(state.HorizontalSpeed, -.5, .5)
                    || !Range(state.ShotInterval, 0, 20) || state.ShotInterval is > 0 and < .4
                    || state.ShotCount is < 1 or > 8 || !Range(state.ShotSpread, 0, 100)
                    || !Range(state.ShotSpeed, .1, .7)
                    || !Range(state.ShotReturnAfterSeconds, 0, 3)
                    || state.ShotReturnAfterSeconds is > 0 and < .4
                    || state.ShotReturnAfterSeconds > 0 && state.ShotInterval == 0
                    || state.ShieldMode is not ("legacy" or "off" or "full" or "rotating")
                    || state.ShieldMode != "legacy" && state.Shield
                    || !Range(state.WeakSpotAngleDegrees, -180, 180)
                    || !Range(state.WeakSpotWidthDegrees, 0, 360)
                    || !Range(state.ShieldRotationDegreesPerSecond, -360, 360)
                    || state.ShieldMode == "rotating" && !Range(state.WeakSpotWidthDegrees, 20, 160)
                    || state.Motion is not ("hold" or "line" or "sine" or "zigzag" or "orbit" or "hunt" or "dash")
                    || state.Motion == "dash" && (state.Duration > 3
                        || Math.Sqrt(state.HorizontalSpeed * state.HorizontalSpeed
                            + enemy.Speed * state.ForwardSpeed * enemy.Speed * state.ForwardSpeed) < .15)) throw Invalid();
            }
            if (!states.Contains(enemy.InitialState) || enemy.States.Any(s => !states.Contains(s.NextState))
                || enemy.States.All(s => ShieldMode(s) == "full")) throw Invalid();
            foreach (var dash in enemy.States.Where(s => s.Motion == "dash"))
                if (dash.Id == enemy.InitialState || !enemy.States.Any(s => s.NextState == dash.Id)
                    || enemy.States.Any(s => s.NextState == dash.Id && !s.Telegraph)) throw Invalid();
            var reachable = new HashSet<string>(StringComparer.Ordinal);
            var route = new List<LivingArcadeEnemyState>();
            var next = enemy.InitialState;
            while (reachable.Add(next))
            {
                var state = enemy.States.Single(s => s.Id == next);
                route.Add(state);
                next = state.NextState;
            }
            if (route.SkipWhile(s => s.Id != next).All(s => ShieldMode(s) == "full")) throw Invalid();
        }
        foreach (var enemy in level.EnemyTypes)
            if (enemy.DeathSpawnCount > 0 && (enemy.DeathSpawnId is null || !ids.Contains(enemy.DeathSpawnId))) throw Invalid();
        // A rare encounter is a spawn decision, independent of how detailed its artwork is.
        var largeTypes = level.EnemyTypes.Where(e => e.EncounterClass == "rare-large").ToDictionary(e => e.Id, StringComparer.Ordinal);
        var largeIds = largeTypes.Keys.ToHashSet(StringComparer.Ordinal);
        if (level.EnemyTypes.Any(e => e.DeathSpawnCount > 0 && largeIds.Contains(e.DeathSpawnId!))
            || level.Waves.Where(w => w is not null && largeIds.Contains(w.EnemyId))
                .Any(w => w.Count != 1 || !Range(w.X, largeTypes[w.EnemyId].Radius, 1 - largeTypes[w.EnemyId].Radius))
            || level.MiniEvents.Where(m => m is not null && m.Kind == "ambush" && largeIds.Contains(m.EnemyId!))
                .Any(m => !Range(m.X, largeTypes[m.EnemyId!].Radius, 1 - largeTypes[m.EnemyId!].Radius))
            || level.Waves.Where(w => w is not null && largeIds.Contains(w.EnemyId)).Sum(w => w.Count)
                + level.MiniEvents.Count(m => m is not null && m.Kind == "ambush" && largeIds.Contains(m.EnemyId!)) > 1) throw Invalid();
        foreach (var enemy in level.EnemyTypes) CheckSpawnChain(enemy.Id, level.EnemyTypes, [], 0);
        if (level.Waves.Sum(w => w?.Count ?? 1000) > 400) throw Invalid();
        foreach (var wave in level.Waves)
            if (wave is null || !ids.Contains(wave.EnemyId) || !Range(wave.At, 0, level.DurationSeconds)
                || wave.Count is < 1 or > 12 || !Range(wave.X, .08, .92)
                || !Range(wave.Spacing, .035, .25) || !Range(wave.SpawnInterval, 0, 5)
                || wave.At + (wave.Count - 1) * wave.SpawnInterval > level.DurationSeconds) throw Invalid();
        foreach (var mini in level.MiniEvents)
            if (mini is null || !Range(mini.At, 0, level.DurationSeconds) || !Range(mini.X, .08, .92)
                || !Range(mini.Strength, .25, 3) || mini.Kind is not ("bonus" or "negative" or "ambush" or "fog" or "meteor")
                || !LivingArcadePowerups.IsValidKind(mini)
                || mini.Kind == "ambush" && (mini.EnemyId is null || !ids.Contains(mini.EnemyId))) throw Invalid();
    }

    public static LivingArcadeHeroState Transfer(LivingArcadeHeroState source, LivingArcadeLevel destination)
    {
        var balanced = LivingArcadeHeroBalance.Normalize(source);
        var carry = destination.ResetDifficulty ? 0 : destination.CarryFraction;
        return balanced with
        {
            WeaponPower = 1 + (balanced.WeaponPower - 1) * carry,
            FireRate = 5 + (balanced.FireRate - 5) * carry,
            SpreadShots = 1,
            MaxHealth = 100 + (balanced.MaxHealth - 100) * carry
        };
    }

    public static LivingArcadeLevel CreateFirst(int seed)
    {
        var level = new LivingArcadeLevel { Seed = seed, Authored = true,
            DescriptionRu = "Освой движение, ускорение времени и удар лопатой. Впереди — дорога с сюрпризами.",
            DescriptionEn = "Learn movement, time powers and the shovel strike. A road of surprises lies ahead.",
            Mechanics = ["aim-by-movement", "alternating-front", "shield-then-fire"] };
        level.EnemyTypes.Add(new());
        level.EnemyTypes.Add(new() { Id = "weaver", TitleRu = "Петляющий", TitleEn = "Weaver", Color = "#FFCC66", Shape = "triangle", Health = 5,
            States = [new() { Motion = "sine", HorizontalSpeed = .13, Duration = 3, NextState = "dash" },
                new() { Id = "dash", Duration = 1.2, ForwardSpeed = 2.3, NextState = "advance" }] });
        level.EnemyTypes.Add(new() { Id = "guard", TitleRu = "Страж", TitleEn = "Guard", Health = 8, Color = "#C792EA", Shape = "square",
            States = [new() { Shield = true, Duration = 2, ForwardSpeed = .5, NextState = "open" },
                new() { Id = "open", Duration = 3, ShotInterval = 2, ShotCount = 3, ShotSpread = 25, NextState = "advance" }] });
        for (var i = 0; i < 25; i++) level.Waves.Add(new() { At = 5 + i * 4.2,
            EnemyId = i < 6 ? "scout" : i % 5 == 0 ? "guard" : i % 3 == 0 ? "weaver" : "scout",
            Count = i < 4 ? 2 : 3, X = i % 2 == 0 ? .3 : .7, Spacing = .09 });
        level.MiniEvents = [new() { At = 15, X = .5, Tutorial = true }, new() { At = 39, X = .25, Tutorial = true },
            new() { At = 64, X = .75, Tutorial = true }, new() { At = 91, X = .5, Tutorial = true }];
        Validate(level);
        return level;
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            { if (!names.Add(property.Name)) throw Invalid(); RejectDuplicateProperties(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicateProperties(child);
    }

    private static void CheckSpawnChain(string id, List<LivingArcadeEnemyDefinition> enemies, HashSet<string> chain, int depth)
    {
        if (!chain.Add(id) || depth > 4) throw Invalid();
        var enemy = enemies.Single(e => e.Id == id);
        if (enemy.DeathSpawnCount > 0) CheckSpawnChain(enemy.DeathSpawnId!, enemies, chain, depth + 1);
        chain.Remove(id);
    }
    private static bool Id(string? text) => text is { Length: > 0 and <= 48 } && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
    private static bool Text(string? text, int maximum) => !string.IsNullOrWhiteSpace(text) && text.Length <= maximum;
    private static bool Range(double n, double min, double max) => double.IsFinite(n) && n >= min && n <= max;
    private static bool Color(string? value) => value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);
    internal static string ShieldMode(LivingArcadeEnemyState state) => state.ShieldMode == "legacy"
        ? state.Shield ? "full" : "off" : state.ShieldMode;
    private static InvalidDataException Invalid() => new("LivingArcade.InvalidLevel");
}
