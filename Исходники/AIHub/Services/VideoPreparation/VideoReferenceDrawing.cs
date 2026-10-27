using System.IO;
using AIHub.Models;

namespace AIHub.Services.VideoPreparation;

public sealed class VideoReferenceDrawing
{
    public string ModelId { get; set; } = "z-image";
    public List<VideoReferenceVariant> Variants { get; set; } = [];
}
public sealed record VideoReferenceVariant(string Id, string PromptId, string ImageId, string Prompt,
    string Definition, string CardId, string Purpose, string SourceDescription, string ModelId,
    long Seed, OutputModelUse[] Models, ImageGenerationRuntimeReceipt? Runtime);

public static class VideoReferenceDrawings
{
    public static VideoReferenceDrawing Ensure(VideoPreparationProject p) => VideoReferences.Ensure(p).Drawing ??= new();
    public static bool IsCurrent(VideoPreparationProject p, VideoReferenceVariant v) => p.References?.Plan?.Items
        .Any(i => i.Id == v.PromptId && i.English == v.Prompt && VideoReferencePlans.Definition(i) == v.Definition) == true
        && VideoReferences.Description(p, v.CardId) == v.SourceDescription;
    public static bool IsSelected(VideoPreparationProject p, VideoReferenceVariant v) => p.References?.Bindings
        .Any(b => b.ImageId == v.ImageId && b.CardId == v.CardId && b.Purpose == v.Purpose) == true;
    public static void Select(VideoPreparationProject p, VideoReferenceVariant v, bool selected)
    {
        if (p.References?.Drawing?.Variants.Contains(v) != true) throw new InvalidDataException("VideoPrep.RefInvalidBinding");
        if (!selected) { p.References.Bindings.RemoveAll(b => b.ImageId == v.ImageId && b.CardId == v.CardId && b.Purpose == v.Purpose); return; }
        if (!IsCurrent(p, v)) throw new InvalidDataException("VideoPrep.RefPlanReviewNeeded");
        var binding = VideoReferences.Bind(p, v.ImageId, v.CardId, v.Purpose, "");
        binding.SourceDescription = v.SourceDescription;
    }
    public static bool Ready(VideoPreparationProject p) => p.References?.Plan is { } plan
        && plan.Items.Any(i => i.Selected) && plan.Items.Where(i => i.Selected).All(i =>
            p.References.Drawing?.Variants.Any(v => v.PromptId == i.Id && IsCurrent(p, v) && IsSelected(p, v)) == true);
    public static void Validate(VideoPreparationProject p)
    {
        if (p.References?.Drawing is not { } drawing) return;
        if (!ImageGenerationCatalog.IsAvailable(drawing.ModelId) || drawing.Variants is null || drawing.Variants.Count > 512)
            throw new InvalidDataException("VideoPrep.InvalidArchive");
        var ids = new HashSet<string>(); var images = new HashSet<string>();
        foreach (var v in drawing.Variants)
        {
            if (v is null || !Guid.TryParseExact(v.Id, "N", out _) || !ids.Add(v.Id) || !images.Add(v.ImageId)
                || !Guid.TryParseExact(v.PromptId, "N", out _) || !Guid.TryParseExact(v.ImageId, "N", out _)
                || v.CardId != "" && !Guid.TryParseExact(v.CardId, "N", out _) || !VideoReferences.Purposes.Contains(v.Purpose)
                || v.Prompt is null || v.Prompt.Length is < 1 or > 32000 || v.Definition is null || v.Definition.Length > 100000
                || v.SourceDescription is null || v.SourceDescription.Length > 8_000_000 || v.Models is null || v.Models.Length > 32 || v.Models.Any(m => m is null)
                || !ImageGenerationCatalog.IsAvailable(v.ModelId) || v.Seed < 0
                || !p.References.Images.Any(i => i.Id == v.ImageId && i.Origin == "generated-reference"))
                throw new InvalidDataException("VideoPrep.InvalidArchive");
            _ = new OutputProvenance { Models = v.Models }.Json();
        }
    }
}
