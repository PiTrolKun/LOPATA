using System.Text.Json;

namespace AIHub.Services;

public enum StudioContextMethod { Manual, Retelling, Smart }
public sealed record StudioContextItem(string Id, string Role, string Text);
public sealed record StudioContextMeter(int Input, int Capacity, bool Estimate = false)
{
    public int Available => Math.Max(0, Capacity - LiteraryAutomaticBudget.SafetyTokens - LiteraryAutomaticBudget.MinimumReply);
    public double UsedRatio => Available == 0 ? 0 : Math.Clamp((double)Input / Available, 0, 1);
    public int Free => Math.Max(0, Available - Input);
    public StudioContextMethod Recommended(bool rejected) => rejected ? StudioContextMethod.Smart
        : UsedRatio < .5 ? StudioContextMethod.Manual : StudioContextMethod.Retelling;
}
public sealed class StudioWriterContext
{
    public string Source { get; set; } = "";
    public string Task { get; set; } = "";
    public string Target { get; set; } = "";
    public List<string> Requirements { get; set; } = [];
}
public sealed record StudioContextChange(DateTimeOffset Time, StudioContextMethod Method,
    int? Before, int? After, IReadOnlyList<string> SourceIds);

/// <summary>An immutable view of precisely the editable prompt fields; manuscripts are never editable here.</summary>
public sealed record StudioContextPlan(string Fingerprint, string Session, LiteraryChatProfile Role,
    IReadOnlyList<StudioContextItem> Items)
{
    public static LiteraryStudioState Copy(LiteraryStudioState state) =>
        JsonSerializer.Deserialize<LiteraryStudioState>(ParagraphJson.Encode(state), ParagraphJson.Options)!;
    public static string Stamp(LiteraryStudioState state) => LiteraryWorkIndex.Revision(ParagraphJson.Encode(state));
    public static string WriterSource(LiteraryStudioState state) => LiteraryWorkIndex.Revision(ParagraphJson.Encode(new
        { state.Session, state.Task, state.Result, state.RevisionRequirements }));
    public static StudioWriterContext Writer(LiteraryStudioState state)
    {
        if (state.WriterContext is { } current && current.Source == WriterSource(state))
            return new() { Source = current.Source, Task = current.Task, Target = current.Target, Requirements = current.Requirements.ToList() };
        return new() { Source = WriterSource(state), Task = state.Task, Target = state.Result, Requirements = state.RevisionRequirements.ToList() };
    }
    public static StudioContextPlan Capture(LiteraryStudioState state)
    {
        List<StudioContextItem> items = [];
        if (state.Role == LiteraryChatProfile.Advisor)
            items.AddRange(Conversation(state).Where(m => LiteraryStudioContext.Contains(state, m))
                .Select(m => new StudioContextItem(m.Id, m.Role, m.Text + (m.Quotes.Count == 0 ? "" : "\n" + ParagraphJson.Encode(m.Quotes)))));
        else
        {
            var writer = Writer(state);
            if (writer.Task.Length > 0) items.Add(new("writer/task", "Task", writer.Task));
            if (writer.Target.Length > 0 && (state.Action != "Continue" || state.ContinueFromChat)) items.Add(new("writer/target", "Writer", writer.Target));
            if (writer.Requirements.Count > 0 && state.Action != "Continue")
                items.Add(new("writer/requirements", "User", string.Join("\n\n", writer.Requirements)));
        }
        return new(Stamp(state), state.Session, state.Role, items.ToArray());
    }
    public void EnsureCurrent(LiteraryStudioState state)
    {
        if (state.Session != Session || state.Role != Role || Stamp(state) != Fingerprint)
            throw new InvalidOperationException("Studio.Context.Changed");
    }
    public LiteraryStudioState Apply(LiteraryStudioState state, IReadOnlySet<string> remove, string? summary,
        StudioContextMethod method, int? before, int? after, string eventText)
    {
        EnsureCurrent(state);
        if (remove.Count == 0 || remove.Any(id => !Items.Any(item => item.Id == id)))
            throw new InvalidOperationException("Studio.Context.Empty");
        if (summary is not null && string.IsNullOrWhiteSpace(summary)) throw new InvalidOperationException("Studio.Context.Empty");
        var copy = Copy(state);
        if (Role == LiteraryChatProfile.Advisor)
        {
            foreach (var message in copy.Messages.Where(m => remove.Contains(m.Id))) message.InContext = false;
            if (summary is not null)
            {
                var condensed = copy.Add("Summary", summary);
                // The summary precedes retained recent turns in model chronology; journal order remains creation order.
                condensed.ContextOrder = copy.Messages.Where(m => remove.Contains(m.Id))
                    .Select(m => m.ContextOrder ?? copy.Messages.IndexOf(m)).DefaultIfEmpty(0).Min();
            }
        }
        else
        {
            var writer = Writer(copy);
            if (remove.Contains("writer/task")) writer.Task = "";
            if (remove.Contains("writer/target")) writer.Target = "";
            if (remove.Contains("writer/requirements")) writer.Requirements.Clear();
            if (summary is not null)
            {
                writer.Task = string.Join("\n\n", new[] { writer.Task, summary }.Where(s => s.Length > 0));
                copy.Add("Summary", summary).InContext = false;
            }
            copy.WriterContext = writer;
        }
        var note = copy.Add("ContextEvent", eventText);
        note.InContext = false;
        note.ContextChange = new(DateTimeOffset.UtcNow, method, before, after, remove.ToArray());
        return copy;
    }
    public static IReadOnlyList<StudioMessage> Conversation(LiteraryStudioState state) => state.Messages
        .Select((m, index) => (Message: m, Index: index)).Where(x => x.Message.InContext && x.Message.Complete
            && x.Message.Session == state.Session && x.Message.Role != "ContextEvent")
        .OrderBy(x => x.Message.ContextOrder ?? x.Index).ThenBy(x => x.Index).Select(x => x.Message).ToArray();
}
