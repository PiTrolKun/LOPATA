using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AIHub.Models;

public static class ResumableSessionStatuses
{
    public const string Active = "active";
    public const string Paused = "paused";
    public const string Completed = "completed";
    public const string Recovered = "recovered";
    public const string Unavailable = "unavailable";
}

public static class ResumableSessionStopKinds
{
    public const string None = "none";
    public const string Normal = "normal";
    public const string Completed = "completed";
    public const string Crash = "crash";
}

public sealed class ChoiceScenarioStateCheckpoint
{
    public List<ChoiceScenarioStep> Steps { get; set; } = [];

    public List<bool> StepConsumesAnswer { get; set; } = [];

    public List<ChoiceScenarioAnswer> Answers { get; set; } = [];

    public List<ChoiceCapabilityProfile> ProfileSnapshots { get; set; } = [];

    public ChoiceScenarioStepBudget? StepBudget { get; set; }

    public ChoiceCapabilityProfile CapabilityProfile { get; set; } = new();

    public bool PendingCoreRequest { get; set; }

    public bool PendingCoreRequestFinal { get; set; }

    public bool PendingCoreRequestConsumesAnswer { get; set; }

    public string PendingCoreRequestTrigger { get; set; } = string.Empty;
}

public sealed class ExecutorSessionCheckpoint
{
    public bool BackgroundLoopPending { get; set; }
    public string? PendingRequiredTool { get; set; }
    public string? PendingRequiredTarget { get; set; }
    public bool PendingRequiredToolSatisfied { get; set; }
    public List<StructuredToolCall> PendingToolCalls { get; set; } = [];
    public string InFlightToolCallId { get; set; } = string.Empty;
    public string PendingSnapshotMarkdown { get; set; } = string.Empty;
    public string PendingSnapshotId { get; set; } = string.Empty;
    public ExecutorModelArtifact Artifact { get; set; } = new();

    public ExecutorHandoffPackage Handoff { get; set; } = new();

    public List<StructuredChatMessage> Messages { get; set; } = [];

    public ExecutorTurnResult? LastTurn { get; set; }

    public string CurrentStageId { get; set; } = "task_definition";

    public string ConfirmedBriefCheckpoint { get; set; } = string.Empty;

    public bool BriefConfirmed { get; set; }

    public int SnapshotVersion { get; set; }

    public List<ExecutorResultSnapshot> Snapshots { get; set; } = [];

    public SessionKnowledgeTreeSnapshot KnowledgeTree { get; set; } = new();

    public List<string> EnabledTools { get; set; } = [];

    public List<string> SuccessfulToolCalls { get; set; } = [];

    public ExecutionActionGraph ActionGraph { get; set; } = new();

    public List<ExecutionEvidenceReceipt> EvidenceReceipts { get; set; } = [];
}

public sealed class SessionRestorationContext
{
    public string SessionId { get; set; } = string.Empty;

    public string RunId { get; set; } = string.Empty;

    public int ResumeCount { get; set; }

    public DateTimeOffset OriginalCreatedAt { get; set; }

    public DateTimeOffset RestoredAt { get; set; }

    public string PreviousStopKind { get; set; } = ResumableSessionStopKinds.None;

    public string PreviousStopReason { get; set; } = string.Empty;

    public bool LostUncommittedTurn { get; set; }

    public string LastStableStage { get; set; } = string.Empty;
}

public sealed class ResumableScenarioSession
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string SessionId { get; set; } = string.Empty;

    public string CurrentRunId { get; set; } = string.Empty;

    public string ScenarioId { get; set; } = "uncertainty";

    public string ScenarioName { get; set; } = string.Empty;

    public string CustomTitle { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public int ResumeCount { get; set; }

    public int Revision { get; set; }

    public string Status { get; set; } = ResumableSessionStatuses.Active;

    public string LastStopKind { get; set; } = ResumableSessionStopKinds.None;

    public string LastStopReason { get; set; } = string.Empty;

    public bool IsRunOpen { get; set; }

    public bool LostUncommittedTurn { get; set; }

    public SessionFileManifest FileManifest { get; set; } = new();

    public ChoiceScenarioStateCheckpoint Core { get; set; } = new();

    public string SelectedExecutorModel { get; set; } = string.Empty;

    public ExecutorModelArtifact? ExecutorArtifact { get; set; }

    public ExecutorHandoffPackage? ExecutorHandoff { get; set; }

    public ExecutorSessionCheckpoint? Executor { get; set; }

    public string CoreLogPath { get; set; } = string.Empty;

    public string ExecutorLogPath { get; set; } = string.Empty;

    public string DisplayTitle => string.IsNullOrWhiteSpace(CustomTitle)
        ? ScenarioName
        : CustomTitle;
}

public sealed class ResumableSessionCardViewModel : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _isEditing;
    private string _displayTitle = string.Empty;

    public required ResumableScenarioSession Session { get; init; }

    public string DisplayTitle
    {
        get => _displayTitle;
        set => SetField(ref _displayTitle, value);
    }

    public string CreatedText { get; init; } = string.Empty;

    public string UpdatedText { get; init; } = string.Empty;

    public string StatusText { get; init; } = string.Empty;

    public string PrimaryActionText { get; init; } = string.Empty;

    public string RenameTooltip { get; init; } = string.Empty;

    public bool RequiresExecutorDownload { get; init; }

    public bool CanResume { get; init; } = true;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (SetField(ref _isEditing, value))
            {
                OnPropertyChanged(nameof(IsTitleReadOnly));
            }
        }
    }

    public bool IsTitleReadOnly => !IsEditing;

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
