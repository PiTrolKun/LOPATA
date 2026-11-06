using System.Globalization;
using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.LivingArcade;

public sealed partial class LivingArcadeRunStore
{
    internal LivingArcadeBossArtReceipt? GetBossArt(string id, int levelNumber)
    {
        lock (_gate)
        {
            var run = Load(id);
            return run is null ? null : ReadBossArtReceipt(run, levelNumber);
        }
    }

    internal bool TrySaveBossArt(string id, int levelNumber, LivingArcadeBossArtReceipt result)
    {
        lock (_gate)
        {
            var run = Load(id);
            if (run is null || levelNumber > (long)run.CurrentLevel + 2) return false;
            result.Validate(run.Seed, levelNumber);
            var confirmed = ReadBossArtReceipt(run, levelNumber) ?? result;
            var receiptPath = BossArtReceiptPath(id, levelNumber);
            var receiptChanged = !File.Exists(receiptPath)
                || new FileInfo(receiptPath).Length > 500_000
                || File.ReadAllText(receiptPath) != JsonSerializer.Serialize(confirmed, LivingArcadeLevelPolicy.JsonOptions);
            if (receiptChanged)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
                AtomicWrite(receiptPath, confirmed);
            }
            var changed = false;
            if (run.Levels.TryGetValue(levelNumber, out var existing) && !SameBossArt(existing.BossArt, confirmed.Artwork))
            {
                run.Levels[levelNumber] = existing with { BossArt = LivingArcadeBossArtPolicy.Copy(confirmed.Artwork) };
                changed = true;
            }
            var archive = Path.Combine(RunDirectory(id), "levels", levelNumber.ToString(CultureInfo.InvariantCulture) + ".json");
            if (File.Exists(archive))
            {
                Lopata.Updates.SafeUpdatePath.RejectLinks(archive);
                if (new FileInfo(archive).Length > 10_000_000) throw new InvalidDataException("LivingArcade.InvalidSave");
                var original = LivingArcadeLevelPolicy.Parse(File.ReadAllText(archive));
                if (original.Number != levelNumber) throw new InvalidDataException("LivingArcade.InvalidSave");
                if (!SameBossArt(original.BossArt, confirmed.Artwork))
                {
                    original.BossArt = LivingArcadeBossArtPolicy.Copy(confirmed.Artwork);
                    AtomicWrite(archive, original);
                }
            }
            if (changed) { run.Revision++; Write(run); }
            return receiptChanged || changed;
        }
    }

    private LivingArcadeBossArtReceipt? ReadBossArtReceipt(LivingArcadeRun run, int levelNumber)
    {
        if (levelNumber < 3 || levelNumber % 3 != 0) return null;
        var path = BossArtReceiptPath(run.Id, levelNumber);
        var existing = run.Levels.GetValueOrDefault(levelNumber)?.BossArt;
        if (File.Exists(path))
            return ReadOptionalBossArtReceipt(path, run.Seed, levelNumber)
                ?? ReadOptionalBossArtReceipt(path + ".bak", run.Seed, levelNumber)
                ?? (existing is null ? new(run.Seed, levelNumber, "failed", Failure: "UnreadableOptionalReceipt")
                    : new(run.Seed, levelNumber, "generated", LivingArcadeBossArtPolicy.Copy(existing)));
        return existing is null ? null : new(run.Seed, levelNumber, "generated", LivingArcadeBossArtPolicy.Copy(existing));
    }

    private static LivingArcadeBossArtReceipt? ReadOptionalBossArtReceipt(string path, int seed, int level)
    {
        if (!File.Exists(path)) return null;
        Lopata.Updates.SafeUpdatePath.RejectLinks(path);
        try
        {
            if (new FileInfo(path).Length > 500_000) return null;
            var result = JsonSerializer.Deserialize<LivingArcadeBossArtReceipt>(File.ReadAllText(path), LivingArcadeLevelPolicy.JsonOptions);
            result?.Validate(seed, level); return result;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException) { return null; }
    }

    private string BossArtReceiptPath(string id, int level) => Path.Combine(RunDirectory(id), "boss-art",
        level.ToString(CultureInfo.InvariantCulture) + ".json");

    private static bool SameBossArt(LivingArcadeBossArt? first, LivingArcadeBossArt? second) =>
        JsonSerializer.Serialize(first, LivingArcadeLevelPolicy.JsonOptions) == JsonSerializer.Serialize(second, LivingArcadeLevelPolicy.JsonOptions);
}
