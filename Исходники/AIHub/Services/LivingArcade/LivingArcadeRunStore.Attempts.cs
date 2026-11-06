using System.IO;
using System.Security.Cryptography;
using System.Text;
using AIHub.Models;

namespace AIHub.Services.LivingArcade;

public sealed partial class LivingArcadeRunStore
{
    /// <summary>Commits the actual event sequence once, before the first combat tick.</summary>
    public LivingArcadeAttempt GetOrCreateAttempt(string id)
    {
        lock (_gate)
        {
            var run = Load(id) ?? throw new InvalidDataException("LivingArcade.DeletedRun");
            if (run.Attempt is { } existing)
            {
                if (existing.SimulationVersion == "fixed60-v4") return existing;
                // Preserve the original attempt before recording which simulation now plays it.
                // Its seed and randomized model events stay unchanged across the upgrade.
                ArchiveAttempt(RunDirectory(id), existing);
                run.Attempt = existing with { SimulationVersion = "fixed60-v4",
                    MiniEvents = existing.MiniEvents.Select(mini => mini with { }).ToList() };
                run.Revision++; Write(run); return run.Attempt;
            }
            if (run.PendingHero is not null || !run.Levels.TryGetValue(run.CurrentLevel, out var level)
                || run.CurrentLevel == 1 && !run.FirstEventsPrepared)
                throw new InvalidOperationException("LivingArcade.LevelNotReady");
            var source = Encoding.UTF8.GetBytes(FormattableString.Invariant($"{run.Id}:{run.Seed}:{run.CurrentLevel}:{run.Attempts}:xorshift32-v1"));
            var hash = SHA256.HashData(source);
            var seed = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(hash);
            if (seed == 0) seed = 0x9E3779B9;
            var state = seed;
            double Next()
            {
                state ^= state << 13; state ^= state >> 17; state ^= state << 5;
                return state / (double)uint.MaxValue;
            }
            var events = level.MiniEvents.Select(mini =>
            {
                // The authored teaching bonuses are stable; only model-created decorations vary.
                if (level.Authored && level.Number == 1 && mini.Tutorial) return mini with { };
                return mini with { At = Math.Clamp(mini.At + (Next() * 2 - 1) * 1.5, 0, level.DurationSeconds),
                    X = Math.Clamp(mini.X + (Next() * 2 - 1) * .08, .08, .92),
                    Strength = Math.Clamp(mini.Strength * (.9 + Next() * .2), .25, 3) };
            }).OrderBy(mini => mini.At).ToList();
            run.Attempt = new() { Seed = seed, Level = run.CurrentLevel, Number = run.Attempts + 1, MiniEvents = events };
            run.Revision++; Write(run); return run.Attempt;
        }
    }

    private static void ArchiveAttempt(string directory, LivingArcadeAttempt? attempt)
    {
        if (attempt is null) return;
        var folder = Path.Combine(directory, "attempts");
        Lopata.Updates.SafeUpdatePath.RejectLinks(folder); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{attempt.Level}-{attempt.Number}.json");
        if (!File.Exists(path)) AtomicWrite(path, attempt);
        // Keep the original compatibility record and each simulator's execution recipe.
        // A balance upgrade must not erase either version of an already started attempt.
        var versioned = Path.Combine(folder, $"{attempt.Level}-{attempt.Number}-{attempt.SimulationVersion}.json");
        if (!File.Exists(versioned)) AtomicWrite(versioned, attempt);
    }

    private static void ValidateAttempt(LivingArcadeRun run)
    {
        if (run.Attempt is not { } attempt) return;
        if (attempt.Version != 1 || attempt.Algorithm != "xorshift32-v1"
            || attempt.SimulationVersion is not ("fixed60-v1" or "fixed60-v2" or "fixed60-v3" or "fixed60-v4")
            || attempt.Seed == 0 || attempt.Level != run.CurrentLevel || attempt.Number != run.Attempts + 1
            || attempt.MiniEvents is null || !run.Levels.TryGetValue(run.CurrentLevel, out var level))
            throw new InvalidDataException("LivingArcade.InvalidSave");
        LivingArcadeLevelPolicy.Validate(level with { MiniEvents = attempt.MiniEvents });
    }
}
