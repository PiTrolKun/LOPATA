using System.IO;
using System.Text.Json;

namespace AIHub.Services.VideoPreparation;

public sealed class VideoReferenceImage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string AttachmentRole { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public string Origin { get; set; } = "imported-original";
}
public sealed class VideoReferenceBinding
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ImageId { get; set; } = "";
    public string CardId { get; set; } = "";
    public string CardName { get; set; } = "";
    public string Purpose { get; set; } = "general";
    public string Notes { get; set; } = "";
    public string SourceDescription { get; set; } = "";
}
public sealed class VideoReferenceLibrary
{
    public VideoReferenceDrawing? Drawing { get; set; }
    public VideoReferencePlan? Plan { get; set; }
    public string Choice { get; set; } = "";
    public List<VideoReferenceImage> Images { get; set; } = [];
    public List<VideoReferenceBinding> Bindings { get; set; } = [];
    public string SelectedImageId { get; set; } = "";
    public string DraftCardId { get; set; } = "";
    public string DraftPurpose { get; set; } = "general";
    public string DraftNotes { get; set; } = "";
}

/// <summary>Portable image identities and explicit many-to-many links; no machine paths in reference records.</summary>
public static class VideoReferences
{
    public static readonly string[] Purposes = ["general", "appearance", "place", "object", "style", "composition"];
    public static VideoReferenceLibrary Ensure(VideoPreparationProject p) => p.References ??= new();
    public static string? Description(VideoPreparationProject p, string cardId)
    {
        var cards = p.World?.Cards;
        if (cardId.Length > 0 && cards?.Any(c => c.Id == cardId) != true) return null;
        return JsonSerializer.Serialize((cards ?? []).Where(c => cardId.Length == 0 || c.Id == cardId)
            .Select(c => new { c.Kind, c.Name, Description = c.Text.IdeaText, c.Fixed }));
    }
    public static VideoReferenceBinding Bind(VideoPreparationProject p, string imageId, string cardId, string purpose, string notes)
    {
        var r = Ensure(p);
        if (!r.Images.Any(i => i.Id == imageId) || !Purposes.Contains(purpose) || notes.Length > 10000)
            throw new InvalidDataException("VideoPrep.RefInvalidBinding");
        var description = Description(p, cardId) ?? throw new InvalidDataException("VideoPrep.RefMissingCard");
        var binding = r.Bindings.FirstOrDefault(b => b.ImageId == imageId && b.CardId == cardId && b.Purpose == purpose);
        if (binding is null)
        {
            if (r.Bindings.Count >= 4096) throw new InvalidDataException("VideoPrep.RefLimit");
            binding = new() { ImageId = imageId, CardId = cardId, Purpose = purpose };
            r.Bindings.Add(binding);
        }
        binding.CardName = p.World?.Cards.FirstOrDefault(c => c.Id == cardId)?.Name ?? "";
        binding.Notes = notes; binding.SourceDescription = description; return binding;
    }
    public static bool NeedsReview(VideoPreparationProject p, VideoReferenceBinding b) => Description(p, b.CardId) != b.SourceDescription;
    public static void Confirm(VideoPreparationProject p, VideoReferenceBinding b)
    {
        if (p.References?.Bindings.Contains(b) != true) throw new InvalidDataException("VideoPrep.RefInvalidBinding");
        b.SourceDescription = Description(p, b.CardId) ?? throw new InvalidDataException("VideoPrep.RefMissingCard");
        b.CardName = p.World?.Cards.FirstOrDefault(c => c.Id == b.CardId)?.Name ?? "";
    }
    public static string PathFor(VideoPreparationProject p, VideoReferenceImage image) =>
        p.Attachments.Single(a => a.Role == image.AttachmentRole).Path;

    public static void Validate(VideoPreparationProject p)
    {
        if (p.References is not { } r) return;
        static bool Id(string? value) => value is not null && Guid.TryParseExact(value, "N", out _);
        static bool OptionalId(string? value) => value == "" || Id(value);
        if (r.Choice is not ("" or "import" or "create" or "skip") || r.Images is null || r.Bindings is null
            || r.Images.Count > 512 || r.Bindings.Count > 4096 || !OptionalId(r.SelectedImageId) || !OptionalId(r.DraftCardId)
            || !Purposes.Contains(r.DraftPurpose) || r.DraftNotes is null || r.DraftNotes.Length > 10000)
            throw new InvalidDataException("VideoPrep.InvalidArchive");
        var ids = new HashSet<string>();
        foreach (var image in r.Images)
            if (image is null || !Id(image.Id) || !ids.Add(image.Id) || image.Name is null || image.Name.Length > 1024
                || image.Name.Any(char.IsControl) || image.Origin is not ("imported-original" or "generated-reference")
                || image.AttachmentRole != "video-reference/" + image.Id
                || p.Attachments.Count(a => a.Role == image.AttachmentRole) != 1
                || image.Width is < 1 or > 32768 || image.Height is < 1 or > 32768
                || (long)image.Width * image.Height > VideoReferenceFiles.MaxPixels)
                throw new InvalidDataException("VideoPrep.InvalidArchive");
        if (r.SelectedImageId != "" && !r.Images.Any(i => i.Id == r.SelectedImageId)) throw new InvalidDataException("VideoPrep.InvalidArchive");
        VideoReferenceDrawings.Validate(p);
        var bindings = new HashSet<string>(); var pairs = new HashSet<(string, string, string)>();
        foreach (var b in r.Bindings)
            if (b is null || !Id(b.Id) || !bindings.Add(b.Id) || !r.Images.Any(i => i.Id == b.ImageId) || !OptionalId(b.CardId)
                || !Purposes.Contains(b.Purpose) || !pairs.Add((b.ImageId, b.CardId, b.Purpose))
                || b.CardName is null || b.CardName.Length > 1024 || b.Notes is null || b.Notes.Length > 10000
                || b.SourceDescription is null || b.SourceDescription.Length > 8_000_000)
                throw new InvalidDataException("VideoPrep.InvalidArchive");
    }
}
