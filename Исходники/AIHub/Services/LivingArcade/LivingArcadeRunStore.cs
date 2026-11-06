using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.LivingArcade;

/// <summary>Three independent run slots. Atomic manifests and deletion tombstones reject late generation.</summary>
public sealed partial class LivingArcadeRunStore
{
    private static readonly ConcurrentDictionary<string, object> Locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate;
    public string RootDirectory { get; }

    public LivingArcadeRunStore(string? root = null)
    {
        RootDirectory = Path.GetFullPath(root ?? Path.Combine(AppDataPaths.BaseDirectory, "LivingArcade"));
        Lopata.Updates.SafeUpdatePath.RejectLinks(RootDirectory);
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(Path.Combine(RootDirectory, "deleted"));
        _gate = Locks.GetOrAdd(RootDirectory, _ => new object());
    }

    public IReadOnlyList<LivingArcadeRun> List()
    {
        lock (_gate)
            return Directory.EnumerateDirectories(RootDirectory).Select(Path.GetFileName)
                .Where(x => Guid.TryParseExact(x, "N", out _) && (File.Exists(Path.Combine(RootDirectory, x!, "run.json"))
                    || File.Exists(Path.Combine(RootDirectory, x!, "run.json.bak")))).Select(x => Load(x!))
                .Where(x => x is not null).Cast<LivingArcadeRun>().OrderByDescending(x => x.UpdatedUtc).ToArray();
    }

    public LivingArcadeRun Create(string name, int? seed = null)
    {
        lock (_gate)
        {
            if (List().Count >= 3) throw new InvalidOperationException("LivingArcade.SlotsFull");
            var run = new LivingArcadeRun { Name = CleanName(name), Seed = seed ?? Random.Shared.Next() };
            run.Levels[1] = LivingArcadeLevelPolicy.CreateFirst(run.Seed);
            LivingArcadeStarterArt.Apply(run.Levels[1], LivingArcadeLibrary.Load());
            Write(run);
            return run;
        }
    }

    public LivingArcadeRun? Load(string id)
    {
        lock (_gate)
        {
            var directory = RunDirectory(id);
            if (IsDeleted(id) || !Directory.Exists(directory)) return null;
            var path = Path.Combine(directory, "run.json");
            LivingArcadeRun run;
            try { run = Read(path); }
            catch (Exception error) when (error is JsonException or InvalidDataException or FileNotFoundException)
            {
                var previous = path + ".bak";
                if (!File.Exists(previous)) throw;
                run = Read(previous);
                if (run.Id != id) throw new InvalidDataException("LivingArcade.InvalidSave");
                run.RecoveredFromBackup = true;
            }
            if (run.Id != id) throw new InvalidDataException("LivingArcade.InvalidSave");
            return run;
        }
    }

    public void Save(LivingArcadeRun run)
    {
        lock (_gate)
        {
            var stored = Load(run.Id) ?? throw new InvalidDataException("LivingArcade.DeletedRun");
            if (stored.Revision != run.Revision)
                throw new InvalidDataException("LivingArcade.SaveConflict");
            run.RecoveredFromBackup |= stored.RecoveredFromBackup;
            run.Revision = stored.Revision + 1;
            Write(run);
        }
    }

    public void Rename(string id, string name)
    {
        lock (_gate)
        {
            var run = Load(id) ?? throw new InvalidDataException("LivingArcade.DeletedRun");
            run.Name = CleanName(name); run.Revision++; Write(run);
        }
    }

    public LivingArcadeRun Update(string id, Action<LivingArcadeRun> update)
    {
        lock (_gate)
        {
            var run = Load(id) ?? throw new InvalidDataException("LivingArcade.DeletedRun");
            update(run); run.Revision++; Write(run); return run;
        }
    }

    public LivingArcadeRun RecordDefeat(string id, double progress, LivingArcadeHeroState? hero = null) => Update(id, run =>
    {
        if (!double.IsFinite(progress) || progress < 0) throw new ArgumentOutOfRangeException(nameof(progress));
        if (hero is not null)
        {
            ValidateHero(hero);
            run.EntryState = PreserveCombatProgress(run.EntryState, hero);
        }
        run.Attempts++;
        run.Attempt = null;
        var newPenalty = run.Penalty.Length == 0 || progress >= run.DeathProgress;
        if (newPenalty)
            run.Penalty = new[] { "heavy", "weak", "fragile" }[Random.Shared.Next(3)];
        run.DeathProgress = newPenalty ? progress : Math.Max(run.DeathProgress, progress);
    });

    public void RecordExperience(string id, long totalKills)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalKills);
        lock (_gate)
        {
            var run = Load(id);
            if (run is null || run.PendingHero is not null || totalKills <= run.EntryState.TotalKills) return;
            run.EntryState = LivingArcadeHeroBalance.Normalize(run.EntryState) with { TotalKills = totalKills };
            run.Revision++; Write(run);
        }
    }

    public bool ClearPenaltyIfReached(string id, double progress)
    {
        lock (_gate)
        {
            var run = Load(id);
            if (run is null || run.Penalty.Length == 0 || !double.IsFinite(progress) || progress < run.DeathProgress) return false;
            run.Penalty = ""; run.DeathProgress = 0; run.Revision++; Write(run); return true;
        }
    }

    public LivingArcadeRun CompleteLevel(string id, LivingArcadeHeroState hero, LivingArcadeSnapshot snapshot) => Update(id, run =>
    {
        if (run.PendingHero is not null) return;
        ValidateHero(hero);
        if (!snapshot.IsComplete || snapshot.IsDefeated) throw new InvalidOperationException("LivingArcade.LevelNotComplete");
        run.PendingHero = FinishActiveReward(LivingArcadeHeroBalance.Normalize(hero), run.CurrentLevel) with
        { TotalKills = Math.Max(hero.TotalKills, run.EntryState.TotalKills) };
        run.PendingBossRewardLevel = snapshot.BossDefeated ? run.CurrentLevel : null;
        run.History.Add(new() { Level = run.CurrentLevel, Seconds = snapshot.Progress,
            DamageTaken = snapshot.DamageTaken, BonusCount = snapshot.BonusCount, Attempts = run.Attempts,
            Mechanics = run.Levels.GetValueOrDefault(run.CurrentLevel)?.Mechanics.ToList() ?? [] });
    });

    public bool TryAdvanceToReadyLevel(string id, out LivingArcadeRun? advanced)
    {
        lock (_gate)
        {
            var run = Load(id); advanced = run;
            if (run?.PendingHero is null || !run.Levels.TryGetValue(run.CurrentLevel + 1, out var next)) return false;
            if (!TryPrepareRewardedHeroForAdvance(run, out var rewarded)) return false;
            run.EntryState = LivingArcadeLevelPolicy.Transfer(rewarded, next);
            run.PendingHero = null; run.CurrentLevel++; run.Attempts = 0;
            run.PendingBossRewardLevel = null;
            run.Attempt = null;
            run.Penalty = ""; run.DeathProgress = 0; run.Revision++;
            Write(run); advanced = run; return true;
        }
    }

    public bool Delete(string id)
    {
        lock (_gate)
        {
            var directory = RunDirectory(id);
            if (!Directory.Exists(directory) || IsDeleted(id)) return false;
            // Commit deletion identity first: even an interrupted removal cannot resurrect the slot.
            AtomicWrite(Tombstone(id), new { producer = "LOPATA", deletedUtc = DateTimeOffset.UtcNow });
            var prefix = RootDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!directory.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsafe run path.");
            Lopata.Updates.SafeUpdatePath.RejectLinks(directory);
            RejectTreeLinks(directory);
            Directory.Delete(directory, true);
            return true;
        }
    }

    public bool IsDeleted(string id) => File.Exists(Tombstone(id));

    public bool TrySaveLevel(string id, LivingArcadeLevel level)
    {
        LivingArcadeLevelPolicy.Validate(level);
        lock (_gate)
        {
            var run = Load(id);
            if (run is null || level.Number <= run.CurrentLevel || level.Number > run.CurrentLevel + 2) return false;
            if (run.Levels.ContainsKey(level.Number)) return false;
            if (ReadChapterScenery(run, LivingArcadeSceneryArtPolicy.ChapterFor(level.Number)) is { } scenery)
                level = level with { Scenery = LivingArcadeSceneryArtPolicy.Copy(scenery.Artwork) };
            if (ReadBossArtReceipt(run, level.Number) is { } bossArt)
                level = level with { BossArt = LivingArcadeBossArtPolicy.Copy(bossArt.Artwork) };
            run.Levels[level.Number] = level;
            run.Revision++; Write(run); return true;
        }
    }

    public bool TrySaveDraft(string id, LivingArcadeDraft draft)
    {
        if (draft.Number < 1 || draft.Concept is null || draft.Concept.Length > 6000
            || draft.NewMechanic is null || draft.NewMechanic.Length > 1000) throw new InvalidDataException("LivingArcade.InvalidLevel");
        lock (_gate)
        {
            var run = Load(id);
            if (run is null || draft.Number != run.CurrentLevel + 2 || run.Levels.ContainsKey(draft.Number)) return false;
            run.Draft = draft; run.Revision++; Write(run); return true;
        }
    }

    public bool TrySaveFirstMiniEvents(string id, IEnumerable<LivingArcadeMiniEvent> events,
        IEnumerable<LivingArcadeModelCredit>? credits = null)
    {
        lock (_gate)
        {
            var run = Load(id);
            if (run is null || run.CurrentLevel != 1 || run.FirstEventsPrepared
                || !run.Levels.TryGetValue(1, out var first) || !first.Authored) return false;
            var combined = first with { MiniEvents = first.MiniEvents.Concat(events.Select(e => e with { Tutorial = false })).OrderBy(x => x.At).ToList(),
                Credits = first.Credits.Concat(credits ?? []).ToList() };
            LivingArcadeLevelPolicy.Validate(combined);
            run.Levels[1] = combined; run.FirstEventsPrepared = true; run.Revision++; Write(run); return true;
        }
    }

    private void Write(LivingArcadeRun run)
    {
        ValidateRun(run);
        // Reserve space for optional art on every retained level before touching existing files.
        if (JsonSerializer.SerializeToUtf8Bytes(run, LivingArcadeLevelPolicy.JsonOptions).Length > 10_000_000)
            throw new InvalidDataException("LivingArcade.InvalidSave");
        if (IsDeleted(run.Id)) throw new InvalidDataException("LivingArcade.DeletedRun");
        var directory = RunDirectory(run.Id); Directory.CreateDirectory(directory);
        var archive = Path.Combine(directory, "levels"); Directory.CreateDirectory(archive);
        ArchiveAttempt(directory, run.Attempt);
        foreach (var level in run.Levels)
        {
            LivingArcadeLevelPolicy.Validate(level.Value);
            if (level.Key != level.Value.Number) throw new InvalidDataException("LivingArcade.InvalidSave");
            var path = Path.Combine(archive, level.Key.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".json");
            if (!File.Exists(path) || level.Key == 1 && run.FirstEventsPrepared
                && File.ReadAllText(path) != JsonSerializer.Serialize(level.Value, LivingArcadeLevelPolicy.JsonOptions))
                AtomicWrite(path, level.Value);
        }
        // Old originals remain in the archive; the live manifest keeps a bounded rolling window.
        run.Levels = run.Levels.Where(x => x.Key >= run.CurrentLevel - 1 && x.Key <= run.CurrentLevel + 2)
            .ToDictionary(x => x.Key, x => x.Value);
        run.History = run.History.TakeLast(80).ToList();
        run.UpdatedUtc = DateTimeOffset.UtcNow;
        var manifest = Path.Combine(directory, "run.json");
        if (run.RecoveredFromBackup && File.Exists(manifest))
            File.Move(manifest, manifest + ".damaged-" + Guid.NewGuid().ToString("N"));
        AtomicWrite(manifest, run);
        run.RecoveredFromBackup = false;
    }

    private static LivingArcadeRun Read(string path)
    {
        Lopata.Updates.SafeUpdatePath.RejectLinks(path);
        if (new FileInfo(path).Length > 10_000_000) throw new InvalidDataException("LivingArcade.InvalidSave");
        var json = File.ReadAllText(path);
        using (var document = JsonDocument.Parse(json))
            if (document.RootElement.TryGetProperty("schemaVersion", out var schema)
                && schema.ValueKind == JsonValueKind.Number && schema.GetInt32() > 1)
                throw new NotSupportedException("LivingArcade.InvalidSave");
        var run = JsonSerializer.Deserialize<LivingArcadeRun>(json, LivingArcadeLevelPolicy.JsonOptions)
            ?? throw new InvalidDataException("LivingArcade.InvalidSave");
        ValidateRun(run);
        foreach (var level in run.Levels)
        {
            if (level.Key != level.Value.Number) throw new InvalidDataException("LivingArcade.InvalidSave");
            LivingArcadeLevelPolicy.Validate(level.Value);
        }
        return run;
    }

    private static void ValidateRun(LivingArcadeRun run)
    {
        if (run.SchemaVersion != 1 || !Guid.TryParseExact(run.Id, "N", out _)
            || string.IsNullOrWhiteSpace(run.Name) || run.Name.Length > 80 || run.CurrentLevel < 1
            || run.Attempts < 0 || run.EntryState is null || run.Levels is null || run.History is null
            || !double.IsFinite(run.DeathProgress) || run.DeathProgress < 0 || run.Penalty is null
            || run.Penalty is not ("" or "heavy" or "weak" or "fragile")) throw new InvalidDataException("LivingArcade.InvalidSave");
        if (!run.Levels.ContainsKey(run.CurrentLevel)) throw new InvalidDataException("LivingArcade.InvalidSave");
        ValidateHero(run.EntryState);
        if (run.PendingHero is not null) ValidateHero(run.PendingHero);
        ValidateAttempt(run);
        ValidateRunRewards(run);
    }

    private static void ValidateHero(LivingArcadeHeroState hero)
    {
        LivingArcadeCombatRules.ValidateHeroCombat(hero);
        if (hero.PowerupBalanceVersion is < 0 or > LivingArcadeHeroBalance.Version || hero.TotalKills < 0
            || !double.IsFinite(hero.PermanentPower) || hero.PermanentPower < 1 || hero.PermanentPower > 100
            || !double.IsFinite(hero.WeaponPower) || hero.WeaponPower < 1 || hero.WeaponPower > 100
            || !double.IsFinite(hero.MaxHealth) || hero.MaxHealth is < 1 or > 1000
            || !double.IsFinite(hero.FireRate) || hero.FireRate is < 1 or > 20
            || hero.SpreadShots is < 1 or > 5) throw new InvalidDataException("LivingArcade.InvalidSave");
    }

    private string RunDirectory(string id)
    {
        RequireId(id);
        var path = Path.Combine(RootDirectory, id); Lopata.Updates.SafeUpdatePath.RejectLinks(path); return path;
    }
    private string Tombstone(string id) { RequireId(id); return Path.Combine(RootDirectory, "deleted", id + ".json"); }
    private static void RequireId(string id)
    { if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("LivingArcade.InvalidSave"); }
    private static string CleanName(string value)
    {
        var name = value.Trim();
        if (name.Length is < 1 or > 80 || name.Any(char.IsControl)) throw new InvalidDataException("LivingArcade.InvalidName");
        return name;
    }
    private static void RejectTreeLinks(string directory)
    {
        var remaining = new Stack<string>(); remaining.Push(directory);
        while (remaining.TryPop(out var current))
            foreach (var child in Directory.EnumerateFileSystemEntries(current))
            {
                var attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked save entry.");
                if ((attributes & FileAttributes.Directory) != 0) remaining.Push(child);
            }
    }
    private static void AtomicWrite<T>(string path, T value)
    {
        Lopata.Updates.SafeUpdatePath.RejectLinks(path);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, LivingArcadeLevelPolicy.JsonOptions);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak"); else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
