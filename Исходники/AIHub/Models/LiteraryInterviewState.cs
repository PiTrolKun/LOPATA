namespace AIHub.Models;

public enum InterviewStage { Mandatory, Adaptive, Understanding, Correction, Review }
public sealed class LiteraryInterviewState
{
    public int Version { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Parent { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string Language { get; set; } = "ru";
    public int Step { get; set; } = 1;
    public InterviewStage Stage { get; set; }
    public bool AdaptiveAnswer { get; set; }
    public bool AiDisabled { get; set; }
    public bool LimitNoticePending { get; set; }
    public bool InFlight { get; set; }
    public string AdaptiveQuestion { get; set; } = "";
    public string AdaptiveInput { get; set; } = "";
    public string Pending { get; set; } = "";
    public string Correction { get; set; } = "";
    public string PendingId { get; set; } = "";
    public DateTimeOffset? SavedAt { get; set; }
    public int PromptTokens { get; set; }
    public int? ConfirmationReturnStep { get; set; }
    public Dictionary<int, string> Inputs { get; set; } = [];
    public Dictionary<int, string> Selections { get; set; } = [];
    public List<InterviewRecord> Records { get; set; } = [];
    public List<InterviewAsked> Asked { get; set; } = [];
    public List<int> CompletedTopics { get; set; } = [];
    public List<string> Materials { get; set; } = [];
    public List<InterviewRouteRow> Route { get; set; } = Enumerable.Range(1, 10).Select(n => new InterviewRouteRow { Number = n }).ToList();
    public List<InterviewJournalItem> Journal { get; set; } = [];
    public bool Finished { get; set; }
    public string? InitialPositive { get; set; }
    public string? InitialNegative { get; set; }
    public Dictionary<int, InterviewNavigationDraft> NavigationDrafts { get; set; } = [];
}
public sealed record InterviewRecord(string Id, int Step, int Topic, string Question, string Raw,
    string Text, string Meaning, bool Adaptive);
public sealed record InterviewAsked(int Topic, string Text);
public sealed record InterviewJournalItem(DateTimeOffset At, string Action, int Step, string Text);
public sealed record InterviewNavigationDraft(InterviewStage Stage, bool AdaptiveAnswer, string Question, string Input,
    string Pending, string Correction, string PendingId, int? ConfirmationReturnStep = null);
public sealed class InterviewRouteRow
{
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
}
