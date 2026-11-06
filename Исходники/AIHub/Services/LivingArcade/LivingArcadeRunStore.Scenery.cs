using System.Globalization;
using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.LivingArcade;

public sealed partial class LivingArcadeRunStore
{
    internal LivingArcadeChapterScenery? GetChapterScenery(string id, int levelNumber)
    {
        lock (_gate)
        {
            var run = Load(id);
            if (run is null) return null;
            return ReadChapterScenery(run, LivingArcadeSceneryArtPolicy.ChapterFor(levelNumber));
        }
    }

    internal bool TrySaveChapterScenery(string id, int levelNumber, LivingArcadeChapterScenery result)
    {
        lock (_gate)
        {
            var run = Load(id);
            if (run is null) return false;
            var chapter = LivingArcadeSceneryArtPolicy.ChapterFor(levelNumber);
            result.Validate(run.Seed, chapter);
            // The first completed result wins. A later retry cannot reroll a chapter's location.
            var confirmed = ReadChapterScenery(run, chapter) ?? result;
            var receiptPath = SceneryReceiptPath(id, chapter);
            var receiptChanged = !File.Exists(receiptPath)
                || File.ReadAllText(receiptPath) != JsonSerializer.Serialize(confirmed, LivingArcadeLevelPolicy.JsonOptions);
            if (receiptChanged)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
                AtomicWrite(receiptPath, confirmed);
            }
            var changed = false;
            foreach (var number in run.Levels.Keys.Where(number => LivingArcadeSceneryArtPolicy.ChapterFor(number) == chapter).ToArray())
            {
                var existing = run.Levels[number];
                if (SameScenery(existing.Scenery, confirmed.Artwork)) continue;
                run.Levels[number] = existing with { Scenery = LivingArcadeSceneryArtPolicy.Copy(confirmed.Artwork) };
                changed = true;
            }
            // Old combat originals remain exact apart from this optional artistic field.
            // This also keeps the first stage of the chapter available after the rolling manifest drops it.
            var first = (long)chapter * 3 + 1;
            for (var offset = 0; offset < 3 && first + offset <= int.MaxValue; offset++)
            {
                var number = (int)(first + offset);
                var archive = Path.Combine(RunDirectory(id), "levels", number.ToString(CultureInfo.InvariantCulture) + ".json");
                if (!File.Exists(archive)) continue;
                Lopata.Updates.SafeUpdatePath.RejectLinks(archive);
                if (new FileInfo(archive).Length > 10_000_000) throw new InvalidDataException("LivingArcade.InvalidSave");
                var original = JsonSerializer.Deserialize<LivingArcadeLevel>(File.ReadAllText(archive), LivingArcadeLevelPolicy.JsonOptions)
                    ?? throw new InvalidDataException("LivingArcade.InvalidSave");
                LivingArcadeLevelPolicy.Validate(original);
                if (SameScenery(original.Scenery, confirmed.Artwork)) continue;
                original.Scenery = LivingArcadeSceneryArtPolicy.Copy(confirmed.Artwork);
                AtomicWrite(archive, original);
            }
            if (changed) { run.Revision++; Write(run); }
            return changed || receiptChanged;
        }
    }

    private LivingArcadeChapterScenery? ReadChapterScenery(LivingArcadeRun run, int chapter)
    {
        var path = SceneryReceiptPath(run.Id, chapter);
        if (File.Exists(path))
        {
            var result = ReadOptionalSceneryReceipt(path, run.Seed, chapter);
            if (result is not null) return result;
            var previous = ReadOptionalSceneryReceipt(path + ".bak", run.Seed, chapter);
            if (previous is not null) return previous;
            var live = run.Levels.Values.FirstOrDefault(level => LivingArcadeSceneryArtPolicy.ChapterFor(level.Number) == chapter
                && level.Scenery is not null)?.Scenery;
            return live is null ? new(run.Seed, chapter, "failed", Failure: "UnreadableOptionalReceipt")
                : new(run.Seed, chapter, "generated", LivingArcadeSceneryArtPolicy.Copy(live));
        }
        var existing = run.Levels.Values.FirstOrDefault(level => LivingArcadeSceneryArtPolicy.ChapterFor(level.Number) == chapter
            && level.Scenery is not null)?.Scenery;
        return existing is null ? null : new(run.Seed, chapter, "generated", LivingArcadeSceneryArtPolicy.Copy(existing));
    }

    private static LivingArcadeChapterScenery? ReadOptionalSceneryReceipt(string path, int seed, int chapter)
    {
        if (!File.Exists(path)) return null;
        Lopata.Updates.SafeUpdatePath.RejectLinks(path);
        try
        {
            if (new FileInfo(path).Length > 500_000) return null;
            var result = JsonSerializer.Deserialize<LivingArcadeChapterScenery>(File.ReadAllText(path), LivingArcadeLevelPolicy.JsonOptions);
            result?.Validate(seed, chapter);
            return result;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        { return null; }
    }

    private string SceneryReceiptPath(string id, int chapter) => Path.Combine(RunDirectory(id), "scenery",
        chapter.ToString(CultureInfo.InvariantCulture) + ".json");

    private static bool SameScenery(LivingArcadeGeneratedScenery? first, LivingArcadeGeneratedScenery? second) =>
        JsonSerializer.Serialize(first, LivingArcadeLevelPolicy.JsonOptions) == JsonSerializer.Serialize(second, LivingArcadeLevelPolicy.JsonOptions);
}
