using System.Drawing;
using System.IO;
using System.Text.Json;

namespace AIHub.Services.ImageLayers;

internal enum LayerMaskMethod { ModelContours, Sam2 }

internal sealed record LayerAnalysisRequest(Rectangle Area, LayerDepth Depth, string? Target, bool Russian,
    LayerMask? First = null, bool KeepContext = false, LayerMaskMethod MaskMethod = LayerMaskMethod.ModelContours)
{
    internal bool PortraitParts { get; init; }
    internal LayerReviewAnchor[]? SceneInventory { get; init; }
}
internal sealed record LayerAnalysisProgress(string Key, int Current = 0, int Total = 0);
internal sealed class LayerAnalysisCheckpoint
{
    public LayerSceneReview? Review { get; set; }
    public LayerMap[]? Map { get; set; }
    public Rectangle? Focus { get; set; }
    public int[]? TargetBox { get; set; }
    public bool TargetIsPerson { get; set; }
    public Dictionary<string, LayerRasterPiece?> Pieces { get; init; } = new(StringComparer.Ordinal);
    public OutputModelUse[] Models { get; set; } = [];
    public int? SkippedMasks { get; set; }
    public int Skipped => SkippedMasks ?? Pieces.Values.Count(piece => piece is null or { Components.Length: 0 });
}

internal interface IImageLayerAnalyzer
{
    Task<LayerProposal> AnalyzeAsync(LayerSource source, LayerAnalysisRequest request,
        IProgress<LayerAnalysisProgress> progress, CancellationToken token,
        LayerAnalysisCheckpoint? checkpoint = null, Action<LayerAnalysisCheckpoint>? save = null);
}

internal sealed class ImageLayerAnalyzer(ManagedModelLibraryStore library) : IImageLayerAnalyzer
{
    public async Task<LayerProposal> AnalyzeAsync(LayerSource source, LayerAnalysisRequest request,
        IProgress<LayerAnalysisProgress> progress, CancellationToken token,
        LayerAnalysisCheckpoint? checkpoint = null, Action<LayerAnalysisCheckpoint>? save = null)
    {
        checkpoint ??= new();
        if (request.MaskMethod == LayerMaskMethod.Sam2)
            return await LayerSamAnalysis.AnalyzeAsync(library, source, request, progress, token, checkpoint, save);

        var (area, depth, target, russian, first, keepContext, _) = request;
        if (target is not null && (depth == LayerDepth.Objects || first is not null)) checkpoint.Map ??= [];
        if (checkpoint.Map is null || target is not null && !checkpoint.Pieces.ContainsKey("$target")
            || checkpoint.Map.Any(entry => !checkpoint.Pieces.ContainsKey(entry.Id)))
            await TraceAsync(source, request, checkpoint, save, progress, token);

        token.ThrowIfCancellationRequested();
        var jobs = new List<LayerRasterJob>();
        if (target is not null)
        {
            var piece = checkpoint.Pieces["$target"];
            jobs.Add(new(new("$target", null, target, [0, 0, 1000, 1000]), piece is null ? [] : [piece], first));
        }

        var map = checkpoint.Map!;
        var groups = map.ToDictionary(item => item.Id);
        var pieces = checkpoint.Pieces;
        foreach (var entry in map.Where(item => !item.Fragment))
        {
            var local = map.Where(part => part.Fragment && RealParent(part.Parent) == entry.Id)
                .Select(PreparePiece).OfType<LayerRasterPiece>().ToArray();
            if (local.Length == 0 && PreparePiece(entry) is { } single) local = [single];
            jobs.Add(new(entry with { Parent = RealParent(entry.Parent) ?? (target is null ? null : "$target") }, local));
        }

        progress.Report(new("Rasterizing"));
        var masks = await LayerRasterBatch.RunAsync(source, area, jobs.ToArray(), token);
        if (!masks.Any(mask => mask.Any)) throw new InvalidDataException("ImageLayers.NoResult");
        return new(jobs.Select((job, i) => new LayerCandidate(job.Layer.Id, job.Layer.Parent, job.Layer.Name, masks[i])).ToArray(),
            checkpoint.Models, checkpoint.Skipped);

        LayerRasterPiece? PreparePiece(LayerMap entry) => pieces[entry.Id] is { Components.Length: > 0 } piece
            ? LayerAnalysisProtocol.RepairInvertedCrop(piece, area, entry.Box) : null;

        string? RealParent(string? id)
        {
            while (id is not null && groups[id].Fragment) id = groups[id].Parent;
            return id;
        }
    }

    private async Task TraceAsync(LayerSource source, LayerAnalysisRequest request, LayerAnalysisCheckpoint checkpoint,
        Action<LayerAnalysisCheckpoint>? save, IProgress<LayerAnalysisProgress> progress, CancellationToken token)
    {
        var (area, depth, target, russian, first, keepContext, _) = request;
        var overview = source.Encode(area, forModel: true);
        await using var vision = new LayerVisionSession(library, gemma: first is not null);
        progress.Report(new(first is null ? "WarmingGamma" : "WarmingGemma"));
        await vision.PrepareAsync(token);
        checkpoint.Models = vision.Models;
        if (checkpoint.Map is null)
        {
            progress.Report(new("Mapping"));
            checkpoint.Map = await vision.MapAsync(overview, depth, target, russian, token, review: request.SceneInventory);
            save?.Invoke(checkpoint);
        }
        if (target is not null && !checkpoint.Pieces.ContainsKey("$target"))
        {
            if (checkpoint.Focus is null)
            {
                var frame = area;
                if (!keepContext)
                {
                    var box = await vision.LocateAsync(overview, target, token);
                    if (box is not null && !LayerAnalysisProtocol.NeedsSceneContext(box))
                        frame = LayerAnalysisProtocol.PaddedBox(area, box);
                }
                checkpoint.Focus = frame;
                save?.Invoke(checkpoint);
            }
            var focus = checkpoint.Focus.Value;
            checkpoint.Pieces["$target"] = await ReadPieceAsync(vision, source, focus, target,
                focus == area ? overview : null, area, token);
            save?.Invoke(checkpoint);
        }

        for (var index = 0; index < checkpoint.Map.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var entry = checkpoint.Map[index];
            if (checkpoint.Pieces.ContainsKey(entry.Id)) continue;
            var box = entry.Box;
            var dominant = entry.Parent is null && LayerAnalysisProtocol.NeedsSceneContext(box);
            var frame = dominant ? area : LayerAnalysisProtocol.PaddedBox(area, box);
            progress.Report(new("Tracing", index + 1, checkpoint.Map.Length));
            try
            {
                checkpoint.Pieces[entry.Id] = await ReadPieceAsync(vision, source, frame, entry.Name,
                    dominant ? overview : null, area, token, box);
            }
            catch (Exception error) when (error is JsonException or InvalidDataException or KeyNotFoundException)
            {
                checkpoint.Pieces[entry.Id] = null;
                OwnedProcessRegistry.Log("image_layers_bad_contour", "ImageLayers", detail: error.GetType().Name);
            }
            save?.Invoke(checkpoint);
        }
    }

    private static async Task<LayerRasterPiece> ReadPieceAsync(LayerVisionSession vision, LayerSource source, Rectangle frame,
        string name, byte[]? jpeg, Rectangle area, CancellationToken token, int[]? inventoryBox = null)
    {
        if (frame.Width < 2 || frame.Height < 2) return new(frame, []);
        var answer = await vision.AskAsync(jpeg ?? source.Encode(frame, forModel: true),
            LayerAnalysisProtocol.ShapePrompt(name, frame != area, inventoryBox), LayerAnalysisProtocol.ShapeSchema, 6000, token);
        return new(frame, LayerAnalysisProtocol.ReadShapes(answer));
    }
}
