using System.Text.Json;

namespace AIHub.Models;

public enum BackgroundOperationPhase { Running, Pausing, Paused, Waiting, Countdown, Completed, Failed, Canceled }

/// <summary>A durable logical operation, independent of the page and backend process.</summary>
public sealed record BackgroundOperationState
{
    public int Schema { get; init; } = 1;
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string Kind { get; init; }
    public required string Title { get; init; }
    public string? Project { get; init; }
    public JsonElement Input { get; init; }
    public JsonElement? Checkpoint { get; init; }
    public BackgroundOperationPhase Phase { get; init; } = BackgroundOperationPhase.Running;
    public bool UserPaused { get; init; }
    public bool RequiresDecision { get; init; }
    public bool NeedsAttention { get; init; }
    // A private scenario persists only its active checkpoint, never a completed run record.
    public bool Private { get; init; }
    public BackgroundOperationNotice? Notice { get; init; }
    public IReadOnlyList<BackgroundOperationNotice> Notices { get; init; } = [];
    public double ElapsedSeconds { get; init; }
    public string? Detail { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record BackgroundOperationNotice(string Id, string Kind, string Title, string? Project, bool Private = false);

public sealed class BackgroundOperationWaitingException(string reason) : Exception(reason);
