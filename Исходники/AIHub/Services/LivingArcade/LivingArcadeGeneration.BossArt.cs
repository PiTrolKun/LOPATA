using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.LivingArcade;

public sealed partial class LivingArcadeGeneration
{
    private async Task PrepareOptionalBossArtAsync(LivingArcadeRunStore store, LivingArcadeGenerationRequest request,
        int levelNumber, GenerationCheckpoint checkpoint, Action save, CancellationToken ct)
    {
        if (levelNumber % 3 != 0) return;
        var run = RequireRun(store, request.RunId);
        if (!run.Levels.TryGetValue(levelNumber, out var level)) return;
        checkpoint.BossArtLevels.TryGetValue(levelNumber, out var pending);
        var confirmed = store.GetBossArt(run.Id, levelNumber) ?? pending;
        if (confirmed is null)
        {
            Status("DrawingBoss");
            try
            {
                var library = LivingArcadeLibrary.Load();
                var artwork = await AskAsync(request.Paths, false, BossArtistPrompt, BossArtInput(level, library), 8000,
                    json => ParseGeneratedBossArt(json, level, library), ct, repairs: 1,
                    responseFormat: BossArtistResponseFormat(level, library));
                confirmed = new(run.Seed, levelNumber, "generated", artwork);
            }
            catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
            {
                Trace.TraceWarning("LivingArcade optional boss art fell back to packaged art: {0}", error.GetType().Name);
                confirmed = new(run.Seed, levelNumber, "failed", Failure: error.GetType().Name);
                try { await RetireAsync(); }
                catch (Exception cleanup) when (cleanup is not OperationCanceledException and not OutOfMemoryException)
                { Trace.TraceWarning("LivingArcade boss artist runtime cleanup: {0}", cleanup.GetType().Name); }
            }
        }
        confirmed.Validate(run.Seed, levelNumber);
        checkpoint.BossArtLevels[levelNumber] = confirmed;
        save();
        ct.ThrowIfCancellationRequested();
        if (store.TrySaveBossArt(run.Id, levelNumber, confirmed)) RunUpdated?.Invoke(run.Id);
    }

    private static string BossArtInput(LivingArcadeLevel level, LivingArcadeLibrary library)
    {
        var assets = LivingArcadeBossArtCatalog.Shortlist(library, level.TitleEn + " " + level.DescriptionEn, level.Seed);
        var parts = library.Shortlist(level.TitleEn + " " + level.DescriptionEn, level.Seed, 8, charactersOnly: false);
        return JsonSerializer.Serialize(new
        {
            levelNumber = level.Number, level.TitleEn, level.DescriptionEn,
            scenery = level.Scenery?.TitleEn,
            purpose = "The final three-phase boss is the most detailed visual centerpiece of this chapter. Invent its appearance, not combat rules.",
            artworkCanvas = new { width = 256, height = 256, origin = "top left", center = "128,128",
                libraryFittings = "4 to 16 small distinct shapes, preferably 8 to 12; typical size 6 to 48; maximum 96x96",
                coordinateExample = new { kind = "rect", x = 104, y = 32, width = 48, height = 12, data = (string?)null,
                    fill = "#D6A34C", stroke = "#3A2E20", strokeWidth = 1, opacity = 1 } },
            detailedBases = assets.Select(entry => new { assetId = entry.Id, entry.Title, entry.Tags,
                frameCount = entry.Frames.Length, entry.Animation, actualPalette = library.PaletteFor(entry) }),
            optionalLayerParts = parts.Select(entry => new { assetId = entry.Id, entry.Title, entry.Role, entry.Tags }),
            unchangedRuntime = new { phases = 3, healthAndSize = "fixed by host", collision = "fixed by host",
                prohibition = "No new enemies, shields, attacks, behaviors or executable code in the visual response." }
        }, CompactPlanOptions);
    }

    internal static LivingArcadeBossArt ParseGeneratedBossArt(string json, LivingArcadeLevel level, LivingArcadeLibrary library)
    {
        using (var document = JsonDocument.Parse(json))
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Arcade.Ai.InvalidBossArt");
            foreach (var property in new[] { "titleRu", "titleEn", "visual" })
                if (!document.RootElement.TryGetProperty(property, out _)) throw new InvalidDataException("Arcade.Ai.InvalidBossArt");
        }
        var art = Object<LivingArcadeBossArt>(json);
        art.Source = "model"; art.CreatedUtc = DateTimeOffset.UtcNow;
        art.Credits = [new("boss artist", "Qwen/Qwen2.5-Coder-7B-Instruct-GGUF", LivingArcadeModels.MainRevision)];
        art.AssetCredits = [];
        var shortlist = LivingArcadeBossArtCatalog.Shortlist(library, level.TitleEn + " " + level.DescriptionEn, level.Seed);
        try
        {
            LivingArcadeBossArtGeometryNormalizer.Fit(art);
            LivingArcadeBossArtPolicy.ValidateGeneratedDetail(art, library, shortlist);
        }
        catch (InvalidDataException error) when (error.Data["ValidationDetail"] is not string)
        {
            error.Data["ValidationDetail"] = "Check the drawing contract: use a supplied detailedBases asset, at least four distinct SMALL decorations (usually 6 to 48 pixels), no giant opaque overlay, and unique palette source colors. Coordinates are top-left on 256x256; keep rotated catalog layers inside it. Path metadata x,y,width,height must be zero. Return the complete corrected object.";
            throw;
        }
        var ids = (art.Visual.Mode == "library" ? new[] { art.Visual.AssetId! } : [])
            .Concat(art.Visual.Layers.Select(layer => layer.AssetId)).Distinct(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            var entry = library.Find(id) ?? throw new InvalidDataException("Arcade.Ai.InvalidBossArt");
            var pack = library.Packs.Single(item => item.Id == entry.PackId);
            art.AssetCredits.Add(new(entry.Id, library.AuditRevision, pack.Id, pack.Author, pack.License, pack.SourceUrl,
                entry.Origin?.Models?.Select(model => new LivingArcadeModelCredit(model.Role, model.Id, model.Revision)).ToList() ?? []));
        }
        LivingArcadeBossArtPolicy.Validate(art);
        return art;
    }

    private const string BossArtistPrompt = """
        You are the lead character artist of a retro arcade. Design this chapter's FINAL BOSS: a coherent, richly detailed
        recognizable character, not a triangle or a few primitives. Invent its subject freely. Combat and three phases
        already exist; return only titleRu,titleEn,visual JSON, with no behavior or executable code.

        Prefer mode=library: detailedBases contains illustrated full characters with many original details. Select one,
        preserve its silhouette, and modernize it. Add 4 to 16 distinct SMALL decorations; aim for 8 to 12. Invent relevant
        armor panels, lamps, lenses, crown pieces, joints, rivets, markings or other fitted details. Do not replace the
        illustrated body with an opaque plate. Each fitting normally measures 6 to 48 pixels; maximum 96x96. No repeated
        identical overpainting. Optional catalog layers are small attachments, also at most 96x96, not replacement bodies.
        Use bob/sway/pulse for subtle body motion or cycle for an animated library character. animationPeriod must be 0.5..4;
        use animationPeriod=1.5 EVEN WHEN animation=none or cycle; never zero. Palette swaps are optional;
        [] keeps the original colors. Every palette 'from' color must be UNIQUE. Library frames must be [].

        Alternatively mode=vector: draw an original COMPLETE character, aiming for 48 to 96 meaningful visible shapes;
        minimum 24 distinct visible shapes in every frame, maximum 128. One good complete frame is sufficient, at most 3.
        Include silhouette, head/cockpit, limbs, joints, plates, recesses and highlights suited to your invented subject.
        Vector assetId=null, palette=[]. Never submit an unfinished body or count invisible shapes as detail.

        Use the SAME 256x256 canvas for everything. x,y are TOP LEFT, not center! For example a 48x12 ornament at (104,32)
        ends at (152,44). See artworkCanvas.coordinateExample. Keep every full shape and rotated layer inside the canvas.
        Shapes: rect/ellipse/line/path. Rect/ellipse width,height are positive sizes. Line width,height are endpoint offsets.
        Path data uses geometry M/L/C/Q/Z, not SVG/XML; its coordinates describe its real position, and its metadata must be
        x=0,y=0,width=0,height=0. Small simple rect/ellipse/line ornaments are easier to place than giant paths.
        Fill/stroke are #RRGGBB or none, strokeWidth0..16, opacity0..1. Use only supplied catalog IDs for base/layers.
        Do not output source, credits, timestamps or host-adjustment flags; the host records actual provenance.
        """;
}
