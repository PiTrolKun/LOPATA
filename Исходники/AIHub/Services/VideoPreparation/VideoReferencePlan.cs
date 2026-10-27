using System.IO;
using System.Text.Json;

namespace AIHub.Services.VideoPreparation;

public sealed class VideoReferencePlan
{
    public string Mode { get; set; } = "";
    public string Page { get; set; } = "list";
    public string ModelId { get; set; } = "";
    public string SelectedId { get; set; } = "";
    public List<VideoReferencePrompt> Items { get; set; } = [];
    public List<VideoReferenceProposal> Proposals { get; set; } = [];
}
public sealed record VideoReferenceProposal(string Id, string Raw, string Context, OutputModelUse Model);
public sealed record VideoReferencePromptVersion(string Id, string Native, string English, string Source,
    string Context, string Definition, DateTimeOffset CreatedUtc, OutputModelUse? Model = null);
public sealed class VideoReferencePrompt
{
    // Additional drawing items use the exact user-entered text, without assistant calls or translation.
    public bool DirectPrompt { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string CardId { get; set; } = "";
    public string Purpose { get; set; } = "general";
    public string Brief { get; set; } = "";
    public bool Selected { get; set; } = true;
    public string Native { get; set; } = "";
    public string English { get; set; } = "";
    public string RevisionId { get; set; } = "";
    public List<VideoReferencePromptVersion> History { get; set; } = [];
}

/// <summary>Append-only bilingual prompt versions. Model replies cannot overwrite an intervening edit.</summary>
public static class VideoReferencePlans
{
    public static VideoReferencePlan Ensure(VideoPreparationProject p) => VideoReferences.Ensure(p).Plan ??= new();
    public static string Context(VideoPreparationProject p) => JsonSerializer.Serialize(new
    {
        concept = p.EnglishText, original = p.IdeaText, development = p.Development?.EnglishText,
        world = p.World?.Working.EnglishText,
        cards = (p.World?.Cards ?? []).Select(c => new { id = c.Id, kind = c.Kind, name = c.Name, description = c.Text.IdeaText, fixedDetails = c.Fixed }),
        existing = (p.References?.Bindings ?? []).Where(b => p.References!.Images.Any(i => i.Id == b.ImageId && i.Origin == "imported-original"))
            .Select(b => new { cardId = b.CardId, purpose = b.Purpose, notes = b.Notes, needsReview = VideoReferences.NeedsReview(p, b) })
    });
    public static string Definition(VideoReferencePrompt item) => JsonSerializer.Serialize(new { item.Name, item.CardId, item.Purpose, item.Brief });
    public static string Snapshot(VideoReferencePrompt item) => JsonSerializer.Serialize(new { definition = Definition(item), item.Native, item.English, item.RevisionId });
    public static VideoReferencePrompt Add(VideoPreparationProject p)
    {
        var plan = Ensure(p);
        if (plan.Items.Count >= 128) throw new InvalidDataException("VideoPrep.RefPlanLimit");
        var item = new VideoReferencePrompt(); plan.Items.Add(item); plan.SelectedId = item.Id; plan.Page = "list"; return item;
    }
    public static VideoReferencePrompt AddDrawingPrompt(VideoPreparationProject p)
    {
        var item = Add(p); item.DirectPrompt = true; Ensure(p).Page = "ready"; return item;
    }
    public static void Capture(VideoPreparationProject p)
    {
        if (p.References?.Plan is not { } plan) return;
        foreach (var item in plan.Items)
        {
            var selected = item.History.FirstOrDefault(v => v.Id == item.RevisionId);
            if (selected is null && item.Native == "" && item.English == "") continue;
            if (selected is not null && selected.Native == item.Native && selected.English == item.English
                && selected.Definition == Definition(item)) continue;
            var version = new VideoReferencePromptVersion(Guid.NewGuid().ToString("N"), item.Native, item.English,
                "user", Context(p), Definition(item), DateTimeOffset.UtcNow, selected?.Model);
            item.History.Add(version); item.RevisionId = version.Id;
        }
    }
    public static bool Ready(VideoPreparationProject p, VideoReferencePrompt item) => item.Name.Trim().Length > 0
        && item.Native.Trim().Length > 0 && item.English.Trim().Length > 0
        && VideoReferences.Description(p, item.CardId) is not null
        && item.History.FirstOrDefault(v => v.Id == item.RevisionId) is { } v
        && v.Native == item.Native && v.English == item.English && v.Definition == Definition(item) && v.Context == Context(p);
    public static bool Ready(VideoPreparationProject p) => Ensure(p).Items.Any(i => i.Selected)
        && Ensure(p).Items.Where(i => i.Selected).All(i => Ready(p, i));
    public static bool Move(VideoPreparationProject p, VideoReferencePrompt item, int direction)
    {
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        var index = item.History.FindIndex(v => v.Id == item.RevisionId);
        var draft = index >= 0 && (item.Native != item.History[index].Native || item.English != item.History[index].English);
        Capture(p);
        index = draft ? direction == -1 ? index : index + 1 : item.History.FindIndex(v => v.Id == item.RevisionId) + direction;
        if (index < 0 || index >= item.History.Count) return false;
        var version = item.History[index]; item.Native = version.Native; item.English = version.English; item.RevisionId = version.Id; return true;
    }
    public static void Apply(VideoPreparationProject p, VideoConceptRequest request, VideoConceptAnswer answer)
    {
        if (p.Id != request.ProjectId) throw new InvalidDataException("VideoPrep.AiWrongProject");
        var plan = Ensure(p);
        if (request.Purpose == VideoReferencePromptProtocol.ListPurpose)
        {
            if (plan.Proposals.Any(v => v.Id == request.Id)) return;
            var proposed = VideoReferencePromptProtocol.ReadList(answer.Text, request.ContextText);
            var additions = proposed.Where(entry => !plan.Items.Any(i => i.Name == entry.Name && i.CardId == entry.CardId && i.Purpose == entry.Purpose))
                .DistinctBy(entry => (entry.Name, entry.CardId, entry.Purpose)).ToArray();
            if (plan.Items.Count + additions.Length > 128 || plan.Proposals.Count >= 1000) throw new InvalidDataException("VideoPrep.RefPlanLimit");
            // Append proposals; discovering again never removes or replaces a manually edited item.
            foreach (var entry in additions)
                plan.Items.Add(new() { Name = entry.Name, CardId = entry.CardId, Purpose = entry.Purpose, Brief = entry.Brief });
            plan.Proposals.Add(new(request.Id, answer.Text, request.ContextText, answer.Model));
            if (plan.SelectedId == "") plan.SelectedId = plan.Items.FirstOrDefault()?.Id ?? "";
        }
        else
        {
            var item = plan.Items.SingleOrDefault(i => i.Id == request.MaterialId) ?? throw new InvalidDataException("VideoPrep.RefPlanChanged");
            if (item.History.Any(v => v.Id == request.Id)) return;
            var reply = VideoReferencePromptProtocol.ReadPrompt(answer.Text);
            var select = request.Text == Snapshot(item) && request.ContextText == Context(p);
            Capture(p);
            using var snapshot = JsonDocument.Parse(request.Text);
            var version = new VideoReferencePromptVersion(request.Id, reply.Native, reply.English, "ai", request.ContextText,
                snapshot.RootElement.GetProperty("definition").GetString()!, DateTimeOffset.UtcNow, answer.Model);
            item.History.Add(version);
            if (select) { item.Native = version.Native; item.English = version.English; item.RevisionId = version.Id; }
        }
        VideoTranslationHistory.UpdateProvenance(p);
    }
    public static IEnumerable<OutputModelUse> Models(VideoReferencePlan plan) => plan.Proposals.Select(v => v.Model)
        .Concat(plan.Items.SelectMany(i => i.History).Where(v => v.Model is not null).Select(v => v.Model!));
    public static void Validate(VideoPreparationProject p)
    {
        if (p.References?.Plan is not { } plan) return;
        static bool Id(string? s) => Guid.TryParseExact(s, "N", out _);
        static bool Text(string? s, int limit) => s is not null && s.Length <= limit;
        if (plan.Mode is not ("" or "assisted" or "manual") || plan.Page is not ("list" or "prompts" or "ready")
            || plan.ModelId is null || plan.ModelId != "" && !VideoConceptModels.Ids.Contains(plan.ModelId)
            || plan.Items is null || plan.Items.Count > 128 || plan.Proposals is null || plan.Proposals.Count > 1000
            || plan.SelectedId is null || plan.SelectedId != "" && !plan.Items.Any(i => i?.Id == plan.SelectedId)) Invalid();
        var ids = new HashSet<string>();
        foreach (var item in plan.Items!)
        {
            if (item is null || !Id(item.Id) || !ids.Add(item.Id) || !Text(item.Name, 1024) || !Text(item.Brief, 10000)
                || item.CardId is null || item.CardId != "" && !Id(item.CardId) || !VideoReferences.Purposes.Contains(item.Purpose)
                || !Text(item.Native, 20000) || !Text(item.English, 20000) || item.History is null || item.History.Count > 10000) Invalid();
            var versions = new HashSet<string>();
            foreach (var v in item!.History!)
            {
                if (v is null || !Id(v.Id) || !versions.Add(v.Id) || !Text(v.Native, 20000) || !Text(v.English, 20000)
                    || !Text(v.Context, 8_000_000) || !Text(v.Definition, 50000) || v.Source is not ("user" or "ai")
                    || v.Source == "ai" && v.Model is null) Invalid();
                if (v!.Model is not null) _ = new OutputProvenance { Models = [v.Model] }.Json();
            }
            if (item.RevisionId is null || item.RevisionId != "" && !versions.Contains(item.RevisionId)) Invalid();
        }
        ids.Clear();
        foreach (var proposal in plan.Proposals!)
        {
            if (proposal is null || !Id(proposal.Id) || !ids.Add(proposal.Id) || !Text(proposal.Raw, 200000)
                || !Text(proposal.Context, 8_000_000) || proposal.Model is null) Invalid();
            _ = new OutputProvenance { Models = [proposal!.Model] }.Json();
        }
    }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Invalid() => throw new InvalidDataException("VideoPrep.InvalidArchive");
}
