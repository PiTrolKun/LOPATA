using System.Diagnostics;
using System.Globalization;

namespace AIHub.Services;

public enum MusicGenerationStage { Idle, Loading, Planning, Sequence, Sound, Encoding, Completed, Cancelled, Error, Paused }

public sealed record MusicGenerationSnapshot(Guid Operation, MusicGenerationStage Stage,
    double? Percent, bool Estimated, TimeSpan Elapsed)
{
    public bool Active => Stage is MusicGenerationStage.Loading or MusicGenerationStage.Planning
        or MusicGenerationStage.Sequence or MusicGenerationStage.Sound or MusicGenerationStage.Encoding;
}

// Presentation telemetry only. The future executor owns the shared background operation.
public sealed class MusicGenerationStatus
{
    private readonly object _gate = new();
    private readonly Func<TimeSpan> _clock;
    private TimeSpan _started;
    private MusicGenerationSnapshot _state = new(Guid.Empty, MusicGenerationStage.Idle, null, false, TimeSpan.Zero);
    public event Action? Changed;

    public MusicGenerationStatus(Func<TimeSpan>? clock = null) =>
        _clock = clock ?? (() => TimeSpan.FromSeconds((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency));

    public MusicGenerationSnapshot Snapshot
    {
        get { lock (_gate) return _state.Active ? _state with { Elapsed = Elapsed() } : _state; }
    }

    public Guid Begin(TimeSpan initialElapsed = default)
    {
        Guid operation;
        lock (_gate)
        {
            operation = Guid.NewGuid(); _started = _clock() - initialElapsed;
            _state = new(operation, MusicGenerationStage.Loading, null, false, initialElapsed);
        }
        Changed?.Invoke(); return operation;
    }

    public bool Report(Guid operation, MusicGenerationStage stage, double? percent = null, bool estimated = false)
    {
        if (stage == MusicGenerationStage.Idle) throw new ArgumentException("Use a new operation to start telemetry.", nameof(stage));
        if (percent is { } value && (!double.IsFinite(value) || value < 0 || value > 100))
            throw new ArgumentOutOfRangeException(nameof(percent));
        lock (_gate)
        {
            if (operation != _state.Operation || !_state.Active) return false;
            // Failure keeps the last reported progress, rather than replacing it with zero.
            if (stage == MusicGenerationStage.Error) { percent = _state.Percent; estimated = _state.Estimated; }
            if (stage == MusicGenerationStage.Completed) { percent = 100; estimated = false; }
            _state = new(operation, stage, percent, estimated, Elapsed());
        }
        Changed?.Invoke(); return true;
    }

    private TimeSpan Elapsed() => TimeSpan.FromTicks(Math.Max(0, (_clock() - _started).Ticks));

    public static string CompactTime(TimeSpan elapsed, string secondsSuffix)
    {
        var total = (long)Math.Max(0, elapsed.TotalSeconds);
        if (total < 60) return total.ToString(CultureInfo.InvariantCulture) + " " + secondsSuffix;
        var seconds = (total % 60).ToString("00", CultureInfo.InvariantCulture).TrimEnd('0');
        return (total / 60).ToString(CultureInfo.InvariantCulture) + "." + (seconds.Length == 0 ? "0" : seconds);
    }
}
