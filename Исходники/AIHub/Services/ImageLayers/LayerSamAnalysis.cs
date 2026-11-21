using System.IO;

namespace AIHub.Services.ImageLayers;

internal static class LayerSamAnalysis
{
    internal static async Task<LayerProposal> AnalyzeAsync(ManagedModelLibraryStore library, LayerSource source,
        LayerAnalysisRequest request, IProgress<LayerAnalysisProgress> progress, CancellationToken token,
        LayerAnalysisCheckpoint checkpoint, Action<LayerAnalysisCheckpoint>? save)
    {
        var area = request.Area;
        var target = request.Target;
        if (target is not null && request.Depth == LayerDepth.Objects) checkpoint.Map ??= [];
        if (checkpoint.Map is null || target is not null && checkpoint.TargetBox is null)
            await LocateAsync(library, source, request, progress, token, checkpoint, save);

        var map = checkpoint.Map!;
        var groups = map.ToDictionary(item => item.Id);
        var located = checkpoint.TargetBox is { } box && target is not null
            ? map.Prepend(new("$target", null, target, box)).ToArray() : map;
        if (located.Length == 0) throw new InvalidDataException("ImageLayers.NoResult");

        progress.Report(new("Masking"));
        var masks = await LayerSamBatch.RunAsync(library, source, area, located, token);
        var byId = located.Select((item, index) => (item.Id, Mask: masks[index]))
            .ToDictionary(item => item.Id, item => item.Mask, StringComparer.Ordinal);
        var candidates = new List<LayerCandidate>();
        if (target is not null)
            candidates.Add(new("$target", null, target, byId.GetValueOrDefault("$target") ?? new(source.Width * source.Height)));

        foreach (var entry in map.Where(item => !item.Fragment))
        {
            var mask = byId[entry.Id];
            foreach (var fragment in map.Where(part => part.Fragment && RealParent(part.Parent) == entry.Id))
                mask = mask.Union(byId[fragment.Id]);
            var bounds = entry.Box;
            var boxArea = (bounds[2] - bounds[0]) * (bounds[3] - bounds[1]);
            candidates.Add(new(entry.Id, RealParent(entry.Parent) ?? (target is null ? null : "$target"),
                entry.Name, mask, boxArea));
        }

        if (!candidates.Any(candidate => candidate.Mask.Any)) throw new InvalidDataException("ImageLayers.NoResult");
        var models = checkpoint.Models.Append(LayerSamBatch.Model).Where(model => model.Id != LayerToolWorker.MediaPipe.Id);
        if (request.PortraitParts)
        {
            var people = map.Where(item => !item.Fragment && item.Person).Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            if (checkpoint.TargetIsPerson) people.Add("$target");
            if (await LayerPortraitParts.AppendAsync(library, source, request, candidates, people, progress, token))
                models = models.Append(LayerToolWorker.MediaPipe);
        }
        checkpoint.Models = models.Distinct().ToArray();
        checkpoint.SkippedMasks = candidates.Count(candidate => !candidate.Mask.Any);
        save?.Invoke(checkpoint);
        return new([.. candidates], checkpoint.Models, checkpoint.Skipped);

        string? RealParent(string? id)
        {
            while (id is not null && groups[id].Fragment) id = groups[id].Parent;
            return id;
        }
    }

    private static async Task LocateAsync(ManagedModelLibraryStore library, LayerSource source, LayerAnalysisRequest request,
        IProgress<LayerAnalysisProgress> progress, CancellationToken token, LayerAnalysisCheckpoint checkpoint,
        Action<LayerAnalysisCheckpoint>? save)
    {
        var overview = source.Encode(request.Area, forModel: true);
        await using var vision = new LayerVisionSession(library, gemma: false);
        progress.Report(new("WarmingGamma"));
        await vision.PrepareAsync(token);
        checkpoint.Models = vision.Models;
        progress.Report(new("Mapping"));
        if (checkpoint.Map is null)
        {
            checkpoint.Map = await vision.MapAsync(overview, request.Depth, request.Target, request.Russian, token,
                request.PortraitParts, request.SceneInventory);
            save?.Invoke(checkpoint);
        }
        if (request.Target is not null && checkpoint.TargetBox is null)
        {
            if (request.PortraitParts)
                (checkpoint.TargetBox, checkpoint.TargetIsPerson) = await vision.LocatePersonAsync(overview, request.Target, token);
            else checkpoint.TargetBox = await vision.LocateAsync(overview, request.Target, token);
            save?.Invoke(checkpoint);
        }
    }
}
