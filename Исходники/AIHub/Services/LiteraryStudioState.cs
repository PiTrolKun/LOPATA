using System.IO;
using System.Text.Json;

namespace AIHub.Services;

public sealed record StudioQuote(string Source, string Text);
public enum StudioSendKeyAction { Advisor, Writer, CurrentRole, NewLine }
public sealed class StudioMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Session { get; set; } = "";
    public string Role { get; set; } = "User";
    public string Text { get; set; } = "";
    public string Action { get; set; } = "Discuss";
    public List<StudioQuote> Quotes { get; set; } = [];
    public bool InContext { get; set; }
    public bool Complete { get; set; } = true;
}

/// <summary>The journal is durable; membership of the model's working context is explicit.</summary>
public sealed class LiteraryStudioState
{
    public int Version { get; set; } = 1;
    public string ProjectId { get; set; } = "";
    public string Session { get; set; } = Guid.NewGuid().ToString("N");
    public LiteraryChatProfile Role { get; set; } = LiteraryChatProfile.Advisor;
    public string Action { get; set; } = "Discuss";
    public string Input { get; set; } = "";
    public string Task { get; set; } = "";
    public string Result { get; set; } = "";
    public string RouteId { get; set; } = "";
    public bool ContinueFromChat { get; set; }
    public bool DirectRequest { get; set; } = true;
    public StudioSendKeyAction EnterAction { get; set; } = StudioSendKeyAction.Advisor;
    public StudioSendKeyAction ControlEnterAction { get; set; } = StudioSendKeyAction.Writer;
    public bool WriterComment { get; set; }
    public bool Interrupted { get; set; }
    public List<StudioMessage> Messages { get; set; } = [];
    public List<StudioQuote> Quotes { get; set; } = [];
    public List<string> RevisionRequirements { get; set; } = [];
    public Dictionary<string, ParagraphSelection> Selection { get; set; } = [];
    public List<ParagraphRecommendation> Recommendations { get; set; } = [];
    public Dictionary<string, string> PromptVariants { get; set; } = [];
    public Dictionary<string, string> RolePromptVariants { get; set; } = [];
    public Dictionary<string, string> SelectedPresets { get; set; } = [];
    public LiteraryPromptSettings? PromptSettings { get; set; }

    public StudioMessage Add(string role, string text, bool complete = true, List<StudioQuote>? quotes = null)
    {
        var message = new StudioMessage { Session = Session, Role = role, Text = text, Action = Action,
            InContext = complete, Complete = complete, Quotes = quotes ?? [] };
        Messages.Add(message); return message;
    }
    public void Transfer(string task)
    {
        foreach (var message in Messages) message.InContext = false;
        Task = task; Result = ""; RevisionRequirements.Clear(); Role = LiteraryChatProfile.Writer; Action = "Continue"; WriterComment = false;
        Add("Task", task);
    }
    public void ReturnToAdvisor()
    {
        foreach (var message in Messages) message.InContext = false;
        Role = LiteraryChatProfile.Advisor; Action = "Discuss"; WriterComment = false;
        var task = Messages.LastOrDefault(m => m.Session == Session && m.Role == "Task" && m.Complete);
        if (task is not null) task.InContext = true;
        var result = Messages.LastOrDefault(m => m.Session == Session && m.Role == "Writer" && m.Complete);
        if (result is not null) result.InContext = true;
    }
    public void Clear()
    {
        foreach (var message in Messages) message.InContext = false;
        Session = Guid.NewGuid().ToString("N"); Role = LiteraryChatProfile.Advisor; Action = "Discuss"; WriterComment = false;
        Input = Task = Result = ""; Quotes.Clear(); RevisionRequirements.Clear(); Recommendations.Clear(); Interrupted = false;
    }
}

public sealed class LiteraryStudioStore(LiteraryProjectLayout layout)
{
    public string FilePath => Path.Combine(layout.Dialogs, "Studio", "session.json");
    private string? _revision;
    public LiteraryStudioState Load()
    {
        layout.EnsurePresent(); LiteraryProjectLayout.CheckTreePath(FilePath);
        var text = File.Exists(FilePath) ? LiteraryChapterFiles.Read(FilePath) : null;
        _revision = text is null ? null : LiteraryWorkIndex.Revision(text);
        if (text is null) return new() { ProjectId = layout.ProjectId };
        var state = JsonSerializer.Deserialize<LiteraryStudioState>(text, ParagraphJson.Options);
        if (state is null || state.Version != 1 || state.ProjectId != layout.ProjectId || !Enum.IsDefined(state.Role)
            || !Enum.IsDefined(state.EnterAction) || !Enum.IsDefined(state.ControlEnterAction)
            || state.Messages is null || state.Selection is null || state.Quotes is null || state.RevisionRequirements is null
            || state.Recommendations is null || state.PromptVariants is null || state.RolePromptVariants is null
            || state.SelectedPresets is null || state.Input is null || state.Task is null || state.Result is null || state.RouteId is null
            || string.IsNullOrWhiteSpace(state.Session) || !LiteraryStudioPrompts.Advisor.Concat(LiteraryStudioPrompts.Writer).Any(a=>a.Id==state.Action)
            || state.Messages.Any(m=>m is null || m.Text is null || m.Quotes is null)
            || state.Selection.Any(s=>s.Value is null))
            throw new InvalidDataException("Invalid studio session. Original file retained.");
        if (state.PromptSettings?.Selected is { } selected) LiteraryPromptSets.Validate([selected]);
        return state;
    }
    public void Save(LiteraryStudioState state)
    {
        layout.EnsurePresent(); layout.EnsureFolder("Dialogs/Studio");
        if (state.ProjectId != layout.ProjectId) throw new InvalidDataException("Foreign studio session.");
        using var writeLock = new FileStream(FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        var revision = File.Exists(FilePath) ? LiteraryWorkIndex.Revision(LiteraryChapterFiles.Read(FilePath)) : null;
        if (revision != _revision) throw new IOException("Studio history was changed by another window. Reopen the project.");
        var text = ParagraphJson.Encode(state); LiteraryChapterFiles.Write(FilePath, text); _revision = LiteraryWorkIndex.Revision(text);
    }
}
