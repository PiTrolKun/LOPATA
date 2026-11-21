using System.Drawing;
using System.IO;
using System.Text.Json;
using AIHub.Models;
using Lopata.Updates;

namespace AIHub.Services.ImageLayers;

internal sealed record LayerAnalysisInput(Guid Document, Guid? Version, Rectangle Area, Guid? Target,
    LayerDepth Depth, bool Refine, bool Russian, bool KeepContext, LayerMaskMethod MaskMethod = LayerMaskMethod.ModelContours,
    bool PortraitParts = false, LayerAdditionalOptions? Extra = null, bool RecognizeText = false)
{
    internal bool Matches(LayerDocument? snapshot) => snapshot is not null && snapshot.Id == Document
        && snapshot.History.LastOrDefault()?.Id == Version;
    internal string Operation => Extra?.Method.Operation() ?? (Refine ? LayerDocument.RefineKind : LayerDocument.AnalyzeKind);
    internal bool CompleteFromCurrentLayers(LayerDocument document) => Extra?.Method == LayerAdditionalMethod.CompleteFrame
        && document.Layers.Values.Any(layer => layer.Mask is not null);
}

internal sealed record LayerAnalysisResult(LayerDocument Preview, int Skipped, LayerSceneReview? Review = null);

internal sealed class LayerAnalysisWork(string directory)
{
    internal const string Kind = "image-layers.analysis";
    internal static LayerAnalysisWork Default => new(Path.Combine(AppDataPaths.BaseDirectory, "Background", "ImageLayers"));

    internal BackgroundOperationState Capture(string title, LayerDocument snapshot, LayerAnalysisInput input)
    {
        if (!input.Matches(snapshot)) throw new InvalidDataException("ImageLayers.InvalidProject");
        var state = new BackgroundOperationState { Kind = Kind, Title = title, Project = snapshot.Id.ToString(),
            Input = JsonSerializer.SerializeToElement(input) };
        var path = FilePath(state.Id, "source");
        Directory.CreateDirectory(directory);
        LayerProjectArchive.Save(path, snapshot);
        return state;
    }

    internal static LayerAnalysisInput ReadInput(BackgroundOperationState state) => state.Kind == Kind
        && state.Input.Deserialize<LayerAnalysisInput>() is { } input && state.Project == input.Document.ToString()
            ? input : throw new InvalidDataException("ImageLayers.InvalidProject");

    internal LayerDocument OpenSource(BackgroundOperationState state)
    {
        var snapshot = LayerProjectArchive.Open(FilePath(state.Id, "source"));
        if (!ReadInput(state).Matches(snapshot)) throw new InvalidDataException("ImageLayers.InvalidProject");
        return snapshot;
    }

    internal bool HasPreview(string id) => File.Exists(FilePath(id, "preview"));

    internal LayerAnalysisResult OpenResult(BackgroundOperationState state)
    {
        var checkpoint = state.Checkpoint?.Deserialize<LayerAnalysisCheckpoint>();
        return new(LayerProjectArchive.Open(FilePath(state.Id, "preview")), checkpoint?.Skipped ?? 0, checkpoint?.Review);
    }

    internal async Task<LayerAnalysisResult> RunAsync(BackgroundOperationState state, LayerDocument snapshot,
        IImageLayerAnalyzer analyzer, IProgress<LayerAnalysisProgress> progress, CancellationToken token,
        ManagedModelLibraryStore? library = null)
    {
        var input = ReadInput(state);
        var savedRetiredResult = input.Extra?.Method == LayerAdditionalMethod.RetiredQwen && HasPreview(state.Id);
        if (!input.Matches(snapshot) || !Enum.IsDefined(input.Depth) || !Enum.IsDefined(input.MaskMethod)
            || input.Refine && input.MaskMethod != LayerMaskMethod.ModelContours || input.Area.Width < 1 || input.Area.Height < 1
            || input.RecognizeText && (input.Refine || !input.Extra.UsesScene() || input.Target is not null || input.Depth == LayerDepth.Objects)
            || !snapshot.Source.Frame.Contains(input.Area)
            || input.Target is Guid target && !snapshot.Layers.ContainsKey(target)
            || input.Refine && (input.Target is not Guid id || snapshot.RefinableVersion(id) is null)
            || !savedRetiredResult && !LayerAdditionalAnalysis.Valid(input, snapshot))
            throw new InvalidDataException("ImageLayers.InvalidProject");
        var checkpoint = state.Checkpoint?.Deserialize<LayerAnalysisCheckpoint>() ?? new();
        using var audit = ComponentAudit.CaptureOperation();
        try
        {
            return await ApplicationBackgroundOperations.RunAsync(Kind, state.Title, state.Project, input,
                ct => Task.Run(() => ExecuteAsync(state, input, snapshot, analyzer, progress, checkpoint, library, ct), ct),
                token, restored: state, releaseModels: () => Task.CompletedTask);
        }
        catch (BackgroundOperationFailedException)
        {
            Forget(state.Id);
            throw;
        }
    }

    private async Task<LayerAnalysisResult> ExecuteAsync(BackgroundOperationState state, LayerAnalysisInput input,
        LayerDocument snapshot, IImageLayerAnalyzer analyzer, IProgress<LayerAnalysisProgress> progress,
        LayerAnalysisCheckpoint checkpoint, ManagedModelLibraryStore? library, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (HasPreview(state.Id)) return OpenResult(state) with { Skipped = checkpoint.Skipped, Review = checkpoint.Review };
        var work = snapshot.Fork();
        try
        {
            var textOnly = input.Extra?.Method == LayerAdditionalMethod.Text;
            var completeFrame = input.Extra?.Method == LayerAdditionalMethod.CompleteFrame;
            var reviewScene = input.Extra?.Method == LayerAdditionalMethod.SceneReview;
            if (input.CompleteFromCurrentLayers(work))
            {
                progress.Report(new("FrameCoverageProgress"));
                var covered = work.PlanFrameBackground(new(work.Layers), input.Russian, token);
                work.Commit(covered, input.Operation, area: input.Area);
                LayerProjectArchive.Save(FilePath(state.Id, "preview"), work);
                return new(work, 0);
            }
            if (!input.Extra.UsesScene() && !textOnly)
            {
                var draft = await LayerAdditionalAnalysis.PlanAsync(library ?? new(), work, input, progress, token);
                token.ThrowIfCancellationRequested();
                work.Commit(draft.Plan, input.Operation, input.Target, input.Area, models: draft.Models);
                LayerProjectArchive.Save(FilePath(state.Id, "preview"), work);
                return new(work, 0);
            }
            var request = new LayerAnalysisRequest(input.Area, input.Depth,
                input.Target is Guid selected ? work.Layers[selected].Name : null, input.Russian,
                input.Refine ? work.Aggregate(input.Target!.Value) : null, input.KeepContext, input.MaskMethod)
            {
                PortraitParts = input.PortraitParts,
                SceneInventory = reviewScene ? LayerSceneReview.Inventory(snapshot, token) : null
            };
            var text = textOnly || input.RecognizeText
                ? await LayerTextAnalysis.RecognizeAsync(library ?? new(), work.Source, input.Area, progress, token)
                : new LayerProposal([], []);
            var proposal = text;
            if (!textOnly)
            {
                try
                {
                    var scene = await analyzer.AnalyzeAsync(work.Source, request, progress, token, checkpoint,
                        value => ApplicationBackgroundOperations.Current?.SaveCheckpoint(value));
                    proposal = new([.. scene.Layers, .. text.Layers], [.. scene.Models, .. text.Models], scene.Skipped + text.Skipped);
                }
                catch (InvalidDataException error) when (error.Message == "ImageLayers.NoResult"
                    && (text.Layers.Length > 0 || completeFrame))
                {
                    proposal = proposal with { Models = [.. checkpoint.Models, .. text.Models] };
                }
            }
            else if (text.Layers.Length == 0)
                throw new InvalidDataException(text.Skipped > 0 ? "ImageLayers.TextAreaTooLarge" : "ImageLayers.NoText");
            token.ThrowIfCancellationRequested();
            progress.Report(new("PreparingComparison"));
            if (reviewScene || completeFrame) proposal = LayerSceneReview.WithoutBackground(proposal);
            var plan = (completeFrame || reviewScene) && !proposal.Layers.Any(layer => layer.Mask.Any) ? new(work.Layers)
                : input.Refine ? work.PlanBoundary(input.Target!.Value, proposal.Layers.Single().Mask)
                : work.PlanAnalysis(proposal, input.Area, input.Target);
            if (completeFrame) plan = work.PlanFrameBackground(plan, input.Russian, token);
            if (reviewScene) checkpoint.Review = LayerSceneReview.Compare(snapshot, proposal, token);
            var first = input.Refine ? work.RefinableVersion(input.Target!.Value) : null;
            var models = first is null ? proposal.Models : first.Models.Concat(proposal.Models).Distinct().ToArray();
            work.Commit(plan, input.Operation, input.Target, input.Area, textOnly ? null : input.Depth.ToString(), models);
            checkpoint.SkippedMasks = proposal.Skipped;
            ApplicationBackgroundOperations.Current?.SaveCheckpoint(checkpoint);
            token.ThrowIfCancellationRequested();
            LayerProjectArchive.Save(FilePath(state.Id, "preview"), work);
            return new(work, proposal.Skipped, checkpoint.Review);
        }
        catch (InvalidDataException error) when (error.Message is "ImageLayers.NoResult" or "ImageLayers.NoText" or "ImageLayers.TextAreaTooLarge"
            or "ImageLayers.ProjectTooLarge" or "ImageLayers.NoSoftEdge" or "ImageLayers.FrameCoverageFailed")
        {
            throw new BackgroundOperationFailedException(error.Message, error);
        }
    }

    internal void Forget(string id)
    {
        foreach (var name in new[] { "preview", "source" })
        {
            try { File.Delete(FilePath(id, name)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { OwnedProcessRegistry.Log("image_layers_cleanup_failed", "ImageLayers", detail: error.GetType().Name); }
        }
    }

    private string FilePath(string id, string name)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("ImageLayers.InvalidProject");
        var path = Path.Combine(directory, $"{id}.{name}{LayerProjectArchive.Extension}");
        SafeUpdatePath.RejectLinks(path);
        return path;
    }
}
