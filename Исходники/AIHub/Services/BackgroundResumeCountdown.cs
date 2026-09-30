namespace AIHub.Services;

/// <summary>Wall clock deadline: sleep does not create a burst of missed timer ticks.</summary>
public sealed class BackgroundResumeCountdown
{
    private DateTimeOffset? _deadline;
    public bool IsActive => _deadline.HasValue;
    public void Start(DateTimeOffset now) => _deadline = now.AddSeconds(60);
    public void Cancel() => _deadline = null;
    public int Remaining(DateTimeOffset now) => _deadline is { } deadline ? Math.Max(0, (int)Math.Ceiling((deadline - now).TotalSeconds)) : 0;
}
