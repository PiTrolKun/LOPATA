namespace Lopata.Inspector;

public sealed record InspectorState(bool Enabled = true, bool FullAudit = true);

public enum InspectorEventKind
{
    Queued, Preparing, Preflight, AttemptStarted, ProcessStarted, Progress, Saved,
    AttemptFinished, OperationFinished, ReleaseRequested, ProcessExited, ReportFinished
}

public enum InspectorOutcome
{
    Unknown, Success, Cancelled, Failure, OutOfVideoMemory, OutOfMemory, SaveFailure,
    Disconnected, CpuRetry, VideoMemoryAdmissionDenied, DriverFailure, WorkerFailure
}

public enum InspectorPreflightStatus { Unknown, NotApplicable, Sufficient, Insufficient }

public sealed record InspectorEvent
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool InMemoryOnly { get; init; }
    public Guid Id { get; init; } = Guid.NewGuid();
    public string OperationId { get; init; } = "";
    public InspectorEventKind Kind { get; init; }
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public string? Scenario { get; init; }
    public string? Engine { get; init; }
    public string? Model { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string? Backend { get; init; }
    public string? Device { get; init; }
    public string? RequestId { get; init; }
    public int ProcessId { get; init; }
    public DateTimeOffset? ProcessStartedUtc { get; init; }
    public string? Adapter { get; init; }
    public long? ExpectedVideoBytes { get; init; }
    public long? AvailableVideoBytes { get; init; }
    public InspectorPreflightStatus PreflightStatus { get; init; }
    public long? ExpectedRamBytes { get; init; }
    public bool? Warm { get; init; }
    public int Attempt { get; init; } = 1;
    public Guid AttemptId { get; init; }
    public string? Detail { get; init; }
    public string? Stage { get; init; }
    public InspectorOutcome Outcome { get; init; }
    public bool UiErrorShown { get; init; }
}

public sealed record InspectorGpuSample(string Adapter, string Name, long? CapacityBytes, long? DedicatedBytes,
    long? SharedBytes, double? UtilizationPercent);

public sealed record InspectorSample(DateTimeOffset TimestampUtc, long? AvailableRamBytes, long? TotalRamBytes,
    double? CpuPercent, InspectorGpuSample[] Gpus)
{
    public string? OperationId { get; init; }
    public int? Attempt { get; init; }
    public Guid AttemptId { get; init; }
    public int? WorkerProcessId { get; init; }
    public string? WorkerAdapter { get; init; }
    public long? WorkerDedicatedBytes { get; init; }
    public long? WorkerSharedBytes { get; init; }
    public double? WorkerUtilizationPercent { get; init; }
}

public sealed record InspectorNotice(string Id, DateTimeOffset TimestampUtc, string OperationId, string Text,
    string? Outcome = null, bool? Helpful = null)
{
    public string Code { get; init; } = "";
    public bool CanRate { get; init; } = true;
    public bool Shown { get; init; } = true;
    public int Attempt { get; init; } = 1;
    public Guid AttemptId { get; init; }
    public string? ExperienceId { get; init; }
    public string? ExperienceContext { get; init; }
    public DateTimeOffset? ExperienceCompletedUtc { get; init; }
}

public sealed record InspectorWorker(int ProcessId, DateTimeOffset StartedUtc);

public sealed record InspectorPreflightResult(bool Acknowledged, string? Warning = null, string? NoticeId = null,
    InspectorPreflightStatus Status = InspectorPreflightStatus.Unknown);

public sealed record InspectorExperience(
    string Id,
    string Context,
    DateTimeOffset CompletedUtc,
    string OperationId,
    int Attempt,
    string Model,
    string Engine,
    string Backend,
    int Width,
    int Height,
    string? AdapterName,
    long? AdapterCapacityBytes,
    bool? Warm,
    double DurationSeconds,
    string Pressure,
    InspectorOutcome Outcome,
    long? PeakDedicatedBytes,
    long? PeakSharedBytes,
    int HelpfulCount = 0,
    int UnhelpfulCount = 0)
{
    public Dictionary<string, bool>? Ratings { get; init; }
}

public sealed record InspectorReport(InspectorEvent[] Events, InspectorSample[] Samples, InspectorNotice[] Notices,
    InspectorExperience[] Experience)
{
    public InspectorSample[] RecentSamples => Samples;
}
