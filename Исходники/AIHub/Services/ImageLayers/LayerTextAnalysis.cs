using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace AIHub.Services.ImageLayers;

internal static class LayerTextAnalysis
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record TextResult(TextArea[] Areas, int Skipped);
    private sealed record TextArea(TextWord[] Words);
    private sealed record TextWord(int Line, TextGlyph[] Glyphs);
    private sealed record TextGlyph(string Value, double Confidence, int Index, int[] Bounds);

    internal static async Task<LayerProposal> RecognizeAsync(ManagedModelLibraryStore library, LayerSource source,
        Rectangle area, IProgress<LayerAnalysisProgress> progress, CancellationToken token)
    {
        progress.Report(new("TextProgress"));
        var card = await ManagedModelRuntimeAudit.VerifyAsync(library, ManagedModelCatalog.ImageLayerTextId, token);
        if (!await LayerRasterRuntime.PrepareAsync(false, null, token, portrait: true))
            throw new InvalidDataException("ImageLayers.Components");
        using var resource = typeof(LayerTextAnalysis).Assembly.GetManifestResourceStream("AIHub.ImageLayers.Text")
            ?? throw new InvalidOperationException("ImageLayers.Components");
        using var reader = new StreamReader(resource);
        var code = await reader.ReadToEndAsync(token);
        // A project stores full-frame packed masks. Bound the number of new masks by that storage cost.
        var limit = (int)Math.Min(1600, 160 * 1024 * 1024L / ((source.Width * source.Height + 7L) / 8));
        var folder = Directory.CreateTempSubdirectory("lopata-text-layers-").FullName;
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(folder, "source.png"), source.Encode(area, forModel: false), token);
            await LayerRasterRuntime.RunAsync(LayerRasterRuntime.PortraitPython, folder, code,
                [folder, card.InstallDirectory, limit.ToString(CultureInfo.InvariantCulture)], token, "ImageLayers.Text", portrait: true);
            var path = Path.Combine(folder, "result.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 4 * 1024 * 1024)
                throw new InvalidDataException("ImageLayers.RasterFailed");
            await using var data = File.OpenRead(path);
            var result = await JsonSerializer.DeserializeAsync<TextResult>(data, Json, token);
            if (result is not { Areas: not null, Skipped: >= 0 } || result.Areas.Length > limit)
                throw new InvalidDataException("ImageLayers.RasterFailed");
            return await ReadGlyphsAsync(result, card.ModelArtifactId, card.DisplayName, card.Revision, source, area, folder, limit, token);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static async Task<LayerProposal> ReadGlyphsAsync(TextResult result, string modelId, string modelName, string revision,
        LayerSource source, Rectangle area, string folder, int maxGlyphs, CancellationToken token)
    {
        var candidates = new List<LayerCandidate>();
        var empty = new LayerMask(source.Width * source.Height);
        var nextGlyph = 0;
        for (var blockIndex = 0; blockIndex < result.Areas.Length; blockIndex++)
        {
            if (result.Areas[blockIndex]?.Words is not { Length: > 0 } words || words.Length > maxGlyphs)
                throw new InvalidDataException("ImageLayers.RasterFailed");
            var areaId = $"$ocr/{blockIndex}";
            var areaMask = empty;
            var text = new List<LayerRecognizedText>(words.Length);
            for (var wordIndex = 0; wordIndex < words.Length; wordIndex++)
            {
                if (words[wordIndex] is not { Glyphs.Length: > 0 } word || word.Glyphs.Length > maxGlyphs - nextGlyph
                    || word.Line < 0 || (wordIndex == 0 ? word.Line != 0 : word.Line < text[^1].Line || word.Line > text[^1].Line + 1))
                    throw new InvalidDataException("ImageLayers.RasterFailed");
                var wordId = $"{areaId}/{wordIndex}";
                var wordMask = empty;
                var letters = new List<LayerRecognizedText>(word.Glyphs.Length);
                foreach (var glyph in word.Glyphs)
                {
                    if (glyph is null || glyph.Index != nextGlyph++ || glyph.Bounds is not [var x, var y, var w, var h]
                        || x < 0 || y < 0 || w < 1 || h < 1 || w > area.Width - x || h > area.Height - y)
                        throw new InvalidDataException("ImageLayers.RasterFailed");
                    var recognized = new LayerRecognizedText(LayerTextKind.Glyph, glyph.Value, letters.Count, word.Line, glyph.Confidence);
                    var shape = await ReadMaskAsync(source, area, folder, glyph.Index, new(x, y, w, h), token);
                    if (!recognized.IsValid() || wordMask.Meets(shape))
                        throw new InvalidDataException("ImageLayers.RasterFailed");
                    letters.Add(recognized);
                    wordMask = wordMask.Union(shape);
                    candidates.Add(new($"{wordId}/{recognized.Order}", wordId, LayerDocument.TextLayerName(recognized), shape, Text: recognized));
                }
                var reading = new LayerRecognizedText(LayerTextKind.Word, string.Concat(letters.Select(item => item.Value)),
                    wordIndex, word.Line, letters.Average(item => item.Confidence));
                text.Add(reading);
                if (areaMask.Meets(wordMask)) throw new InvalidDataException("ImageLayers.RasterFailed");
                areaMask = areaMask.Union(wordMask);
                candidates.Add(new(wordId, areaId, LayerDocument.TextLayerName(reading), wordMask, Text: reading));
            }
            var lines = string.Join('\n', text.GroupBy(word => word.Line).Select(line => string.Join(' ', line.Select(word => word.Value))));
            var block = new LayerRecognizedText(LayerTextKind.Area, lines, blockIndex, 0, text.Average(word => word.Confidence));
            candidates.Add(new(areaId, null, LayerDocument.TextLayerName(block), areaMask, Text: block));
        }
        return new([.. candidates], candidates.Count == 0 ? [] : [new(modelId, modelName, revision, "OCR and glyph coverage")], result.Skipped);
    }

    private static async Task<LayerMask> ReadMaskAsync(LayerSource source, Rectangle area, string folder, int index,
        Rectangle bounds, CancellationToken token)
    {
        var path = Path.Combine(folder, $"output-{index}.bits");
        var length = checked(bounds.Width * bounds.Height);
        if (!File.Exists(path) || new FileInfo(path).Length != (length + 7L) / 8)
            throw new InvalidDataException("ImageLayers.RasterFailed");
        var crop = LayerMask.Unpack(await File.ReadAllBytesAsync(path, token), length);
        if (!crop.Any) throw new InvalidDataException("ImageLayers.RasterFailed");
        return LayerMask.Build(source.Width * source.Height, bits =>
        {
            for (var y = 0; y < bounds.Height; y++)
                for (var x = 0; x < bounds.Width; x++)
                    if (crop[y * bounds.Width + x])
                        bits[(area.Y + bounds.Y + y) * source.Width + area.X + bounds.X + x] = true;
        });
    }
}
