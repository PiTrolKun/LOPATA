using System.IO;
using System.Text.Json;

namespace AIHub.Services.VideoPreparation;

public sealed class VideoWorldCard
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "general";
    public string Name { get; set; } = "";
    public string Fixed { get; set; } = "";
    public VideoPreparationText Text { get; set; } = new();
}
public sealed record VideoWorldRevision(string Id, string Cards, string Context, DateTimeOffset CreatedUtc);
public sealed class VideoWorldOption
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string BatchId { get; set; } = "";
    public int Freedom { get; set; }
    public string Context { get; set; } = "";
    public string BaseRevisionId { get; set; } = "";
    public string Status { get; set; } = "pending";
    public VideoConceptAnswer? Answer { get; set; }
}
public sealed class VideoWorldDocument
{
    public List<VideoWorldCard> Cards { get; set; } = [];
    public List<VideoWorldRevision> History { get; set; } = [];
    public string RevisionId { get; set; } = "";
    public string Context { get; set; } = "";
    public string SelectedCardId { get; set; } = "";
    public string AcceptedOptionId { get; set; } = "";
    public string AcceptedOptionRevisionId { get; set; } = "";
    public List<VideoWorldOption> Options { get; set; } = [];
    public List<OutputModelUse> ModelUses { get; set; } = [];
    public VideoPreparationText Working { get; set; } = new();
}

/// <summary>Stable card identities, reversible whole-world snapshots and proposals separate from accepted content.</summary>
public static partial class VideoWorld
{
    public const string Purpose = "world_options", CardPurpose = "world_card", TranslationPurpose = "world_translation";
    public const string Kind = "video.preparation.world", CardKind = "video.preparation.world_card", TranslationKind = "video.preparation.world_translation";
    public static readonly string[] CardKinds = ["general", "character", "place", "object", "style"];
    public static bool IsStep(string step) => step is "world" or TranslationPurpose;
    public static bool IsRequest(VideoConceptRequest r) => r.Purpose is Purpose or CardPurpose or TranslationPurpose;
    public static string Context(VideoPreparationProject p) => JsonSerializer.Serialize(new
    {
        form = p.Form,
        concept_revision = p.TranslationRevisionId,
        concept = p.EnglishText,
        development_revision = p.Development?.TranslationRevisionId,
        development = p.Development?.EnglishText
    });
    public static bool SourceAvailable(VideoPreparationProject p) => VideoTranslationHistory.IsCurrent(p)
        && p.Development is { } d && VideoTranslationHistory.IsCurrent(d) && VideoDevelopment.SourceCurrent(p)
        && !string.IsNullOrWhiteSpace(d.EnglishText);
    public static bool SourceCurrent(VideoPreparationProject p) => SourceAvailable(p) && p.World?.Context == Context(p);
    public static VideoWorldDocument Ensure(VideoPreparationProject p)
    {
        if (p.World is null)
        {
            p.World = new()
            {
                Context = Context(p),
                Working = new()
                {
                    Id = p.Id,
                    Form = p.Form,
                    AssistantModelId = p.LastAssistantModelId.Length > 0 ? p.LastAssistantModelId : p.Development?.AssistantModelId ?? ""
                }
            };
            AddCard(p, "general"); Capture(p);
        }
        return p.World;
    }
    public static VideoWorldCard AddCard(VideoPreparationProject p, string kind)
    {
        if (!CardKinds.Contains(kind)) throw new InvalidDataException("VideoPrep.WorldInvalidReply");
        var w = p.World ?? throw new InvalidOperationException();
        if (w.Cards.Count >= 64) throw new InvalidDataException("VideoPrep.WorldCardLimit");
        var card = new VideoWorldCard
        {
            Kind = kind,
            Text = new()
            {
                Id = p.Id,
                Form = p.Form,
                AssistantModelId = w.Working.AssistantModelId,
                ContextRevisionId = p.Development?.TranslationRevisionId ?? "",
                ContextText = w.Context
            }
        };
        var empty = new VideoConceptRevision(Guid.NewGuid().ToString("N"), "", "user", null, DateTimeOffset.UtcNow,
            Form: p.Form, ContextRevisionId: card.Text.ContextRevisionId, ContextText: w.Context);
        card.Text.IdeaHistory.Add(empty); card.Text.IdeaRevisionId = empty.Id;
        w.Cards.Add(card); w.SelectedCardId = card.Id; return card;
    }
    public static string SerializeCards(IEnumerable<VideoWorldCard> cards) => JsonSerializer.Serialize(cards);
    public static void Capture(VideoPreparationProject p)
    {
        if (p.World is not { } w) return;
        foreach (var c in w.Cards) VideoConceptHistory.Capture(c.Text);
        w.ModelUses = w.ModelUses.Concat(w.Cards.SelectMany(c => c.Text.IdeaHistory).Where(v => v.Model is not null).Select(v => v.Model!))
            .Concat(w.Working.TranslationHistory.Where(v => v.Model is not null).Select(v => v.Model!)).Distinct().ToList();
        var cards = SerializeCards(w.Cards);
        var selected = w.History.FirstOrDefault(v => v.Id == w.RevisionId);
        if (selected is null || !MatchesSnapshot(selected.Cards, w.Cards) || selected.Context != w.Context)
        { var v = new VideoWorldRevision(Guid.NewGuid().ToString("N"), cards, w.Context, DateTimeOffset.UtcNow); w.History.Add(v); w.RevisionId = v.Id; }
        var text = WorkingText(w.Cards);
        w.Working.Form = p.Form; w.Working.ContextRevisionId = w.RevisionId; w.Working.ContextText = w.Context;
        w.Working.IdeaText = text; VideoConceptHistory.Capture(w.Working); VideoTranslationHistory.Capture(w.Working);
    }
    public static bool Move(VideoPreparationProject p, int direction)
    {
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        var w = p.World!; var previous = w.History.FindIndex(v => v.Id == w.RevisionId);
        var draft = previous >= 0 && (!MatchesSnapshot(w.History[previous].Cards, w.Cards) || w.History[previous].Context != w.Context);
        Capture(p);
        var index = draft ? direction == -1 ? previous : previous + 1 : w.History.FindIndex(v => v.Id == w.RevisionId) + direction;
        if (index < 0 || index >= w.History.Count) return false;
        var v = w.History[index]; w.Cards = JsonSerializer.Deserialize<List<VideoWorldCard>>(v.Cards)!;
        w.Context = v.Context; w.RevisionId = v.Id; w.SelectedCardId = w.Cards.FirstOrDefault()?.Id ?? ""; Capture(p); return true;
    }
    public static VideoPreparationText ForRequest(VideoPreparationProject p, VideoConceptRequest r) => r.Purpose == TranslationPurpose
        ? p.World?.Working ?? throw new InvalidDataException("VideoPrep.WorldMissing")
        : p.World?.Cards.FirstOrDefault(c => c.Id == r.MaterialId)?.Text ?? throw new InvalidDataException("VideoPrep.WorldMissing");
    public static string CardContext(VideoPreparationProject p, VideoWorldCard card) => p.World!.Context + "\n" + card.Kind + "\n" + card.Name + "\n" + card.Fixed
        + "\n" + string.Join("\n", p.World.Cards.Where(c => c.Id != card.Id).Select(c => c.Name + ": " + c.Text.IdeaText));
    public static bool RequestCurrent(VideoPreparationProject p, VideoConceptRequest r) => SourceCurrent(p)
        && r.ContextOriginal == p.World!.Context && (r.Purpose != CardPurpose
            || p.World.Cards.FirstOrDefault(c => c.Id == r.MaterialId) is { } card && CardContext(p, card) == r.ContextText);
    public static void Review(VideoPreparationProject p)
    {
        Capture(p); p.World!.Context = Context(p);
        foreach (var card in p.World.Cards)
        { card.Text.ContextText = p.World.Context; card.Text.ContextRevisionId = p.Development!.TranslationRevisionId; }
        Capture(p);
    }
    public static void Apply(VideoPreparationProject p, VideoWorldOption option)
    {
        var w = Ensure(p);
        option = w.Options.SingleOrDefault(o => o.Id == option.Id) ?? throw new InvalidDataException("VideoPrep.WorldInvalidReply");
        if (!SourceCurrent(p) || option.Status != "ready" || option.Context != Context(p) || option.Answer is null)
            throw new InvalidDataException("VideoPrep.WorldStale");
        var proposal = VideoWorldProtocol.Read(option.Answer.Text);
        Capture(p);
        if (w.AcceptedOptionId == option.Id && w.AcceptedOptionRevisionId == w.RevisionId) return;
        w.Cards = [];
        foreach (var item in proposal.Cards)
        {
            var card = AddCard(p, item.Kind); card.Name = item.Name; card.Fixed = item.Fixed;
            var r = new VideoConceptRequest(Guid.NewGuid().ToString("N"), p.Id, "", card.Text.IdeaRevisionId, "", p.Form, "core", "",
                option.Freedom, VideoWorldProtocol.Version, "", ContextRevisionId: card.Text.ContextRevisionId, ContextText: card.Text.ContextText);
            VideoConceptHistory.Apply(card.Text, r, option.Answer with { Text = item.Description });
        }
        w.SelectedCardId = w.Cards[0].Id; Capture(p);
        w.AcceptedOptionId = option.Id; w.AcceptedOptionRevisionId = w.RevisionId;
    }
    public static IEnumerable<OutputModelUse> Models(VideoWorldDocument w) => w.ModelUses.Concat(w.Options.Where(o => o.Answer is not null).Select(o => o.Answer!.Model))
        .Concat(w.Cards.SelectMany(c => c.Text.IdeaHistory).Where(v => v.Model is not null).Select(v => v.Model!))
        .Concat(w.Working.TranslationHistory.Where(v => v.Model is not null).Select(v => v.Model!));
    public static void Validate(VideoPreparationProject p)
    {
        if (p.World is not { } w) return;
        if (w.Cards is null || w.Cards.Count is < 1 or > 64 || w.History is null || w.History.Count > 10000 || w.Options is null || w.Options.Count > 3000
            || w.Context is null || w.SelectedCardId is null || w.Working is null || w.ModelUses is null || w.ModelUses.Count > 10000
            || w.AcceptedOptionId is null || w.AcceptedOptionRevisionId is null
            || w.AcceptedOptionId != "" && !w.Options.Any(o => o.Id == w.AcceptedOptionId && o.Answer is not null)
            || w.AcceptedOptionRevisionId != "" && !w.History.Any(v => v.Id == w.AcceptedOptionRevisionId))
            throw new InvalidDataException("VideoPrep.InvalidArchive");
        _ = new OutputProvenance { Models = w.ModelUses.ToArray() }.Json();
        ValidateCards(w.Cards, p.Id); w.Working.ValidateText();
        if (w.Working.Id != p.Id || !w.Cards.Any(c => c.Id == w.SelectedCardId)) throw new InvalidDataException("VideoPrep.InvalidArchive");
        var ids = new HashSet<string>();
        foreach (var v in w.History)
        {
            if (v is null || !Guid.TryParseExact(v.Id, "N", out _) || !ids.Add(v.Id) || v.Context is null || v.Cards is null || v.Cards.Length > 2000000)
                throw new InvalidDataException("VideoPrep.InvalidArchive");
            ValidateCards(JsonSerializer.Deserialize<List<VideoWorldCard>>(v.Cards) ?? [], p.Id);
        }
        if (!ids.Contains(w.RevisionId)) throw new InvalidDataException("VideoPrep.InvalidArchive");
        ids.Clear();
        foreach (var o in w.Options)
        {
            if (o is null || !Guid.TryParseExact(o.Id, "N", out _) || !ids.Add(o.Id) || !Guid.TryParseExact(o.BatchId, "N", out _)
                || o.Freedom is < 1 or > 3 || o.Status is not ("pending" or "running" or "ready" or "failed" or "canceled")
                || o.Context is null || !w.History.Any(v => v.Id == o.BaseRevisionId) || o.Status == "ready" && o.Answer is null)
                throw new InvalidDataException("VideoPrep.InvalidArchive");
            if (o.Answer is { } answer) { VideoConceptProtocol.Validate(answer); _ = VideoWorldProtocol.Read(answer.Text); }
        }
    }
    private static void ValidateCards(List<VideoWorldCard> cards, string projectId)
    {
        if (cards.Count is < 1 or > 64 || cards.Select(c => c?.Id).Distinct().Count() != cards.Count) throw new InvalidDataException("VideoPrep.InvalidArchive");
        foreach (var c in cards)
        {
            if (c is null || !Guid.TryParseExact(c.Id, "N", out _) || !CardKinds.Contains(c.Kind) || c.Name is null || c.Name.Length > 512
                || c.Fixed is null || c.Fixed.Length > 10000 || c.Text is null || c.Text.Id != projectId) throw new InvalidDataException("VideoPrep.InvalidArchive");
            c.Text.ValidateText();
        }
    }
}
