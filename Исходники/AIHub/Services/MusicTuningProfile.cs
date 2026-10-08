namespace AIHub.Services;

public sealed record ModelBubblePosition(double X, double Y, bool Approximate, bool CompositionEnabled);

/// <summary>Version 1 engineering trajectories, not calibrated musical quality scores.</summary>
public static class MusicTuningProfile
{
    public const string Id = "YuE2.Native.Q8.Bubble";
    public const string Bf16Id = "YuE2.PyTorch.BF16.Bubble";
    private static readonly double[][] Plan = [[.55, .82, 16], [.60, .86, 24], [.70, .90, 30], [.85, .94, 45], [1, .97, 64]];
    private static readonly double[][] Sequence = [[.8, .88, 50], [.9, .92, 75], [1, .95, 100], [1.1, .97, 140], [1.2, .99, 200]];
    private static readonly string[] Suffixes = ["temperature", "top_p", "top_k"];
    public static ModelTuningState State(MusicExpertSettings settings) => settings.Tuning?.Snapshot() ?? new() {
        Profile = settings.Variation == MusicModelVariants.Bf16 ? Bf16Id : Id };
    public static MusicExpertSettings Move(MusicExpertSettings current, double x, double y, bool suppliedPlan = false)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(x));
        x = Math.Clamp(x, -1, 1); y = Math.Clamp(y, -1, 1);
        var state = State(current); var next = current.Snapshot();
        if (next.Cot != "off" && !suppliedPlan) SetGroup(next, "abc_sampling", Interpolate(Plan, x), state.Pins);
        SetGroup(next, "semantic_sampling", Interpolate(Sequence, y), state.Pins);
        next = next with { Tuning = state with { X = x, Y = y, SimpleModified = true, ExpertModified = true } };
        next.Validate(); return next;
    }
    public static ModelBubblePosition Position(MusicExpertSettings settings, bool suppliedPlan = false)
    {
        var (x, xa) = Project(settings, "abc_sampling", Plan);
        var (y, ya) = Project(settings, "semantic_sampling", Sequence);
        var enabled = settings.Cot != "off" && !suppliedPlan;
        return new(x, y, ya || enabled && xa, enabled);
    }
    public static MusicExpertSettings Guidance(MusicExpertSettings current, double? value)
    {
        var next = current.Snapshot(); next.Values["cfg_scale"] = value ?? -1;
        var state = State(next);
        next = next with { Tuning = state with { Pins = state.Pins.Except(["cfg_scale"]).ToArray(), SimpleModified = true, ExpertModified = true } };
        next.Validate(); return next;
    }
    public static MusicExpertSettings Pin(MusicExpertSettings current, string key)
    {
        var state = State(current);
        return current with { Tuning = state with { Pins = state.Pins.Append(key).Distinct().ToArray(), SimpleModified = true, ExpertModified = true } };
    }
    public static MusicExpertSettings Release(MusicExpertSettings current, string key)
    {
        var state = State(current);
        var next = current.Snapshot() with { Tuning = state with { Pins = state.Pins.Except([key]).ToArray(), SimpleModified = true, ExpertModified = true } };
        if (key == "cfg_scale") next.Values[key] = -1;
        else if (key.StartsWith("abc_sampling.", StringComparison.Ordinal) && Suffixes.Any(s => key.EndsWith("." + s, StringComparison.Ordinal))) {
            var point = Interpolate(Plan, state.X ?? Position(current).X);
            next.Values[key] = point[Array.IndexOf(Suffixes, key[(key.LastIndexOf('.') + 1)..])];
        } else if (key.StartsWith("semantic_sampling.", StringComparison.Ordinal) && Suffixes.Any(s => key.EndsWith("." + s, StringComparison.Ordinal))) {
            var point = Interpolate(Sequence, state.Y ?? Position(current).Y);
            next.Values[key] = point[Array.IndexOf(Suffixes, key[(key.LastIndexOf('.') + 1)..])];
        } else next.Values[key] = MusicModelVariants.Defaults(current.Variation).Get(key);
        next.Validate(); return next;
    }
    public static MusicExpertSettings ResetCircle(MusicExpertSettings current) => Move(current, 0, 0);
    private static void SetGroup(MusicExpertSettings next, string prefix, double[] values, string[] pins)
    {
        for (var i = 0; i < Suffixes.Length; i++) {
            var key = prefix + "." + Suffixes[i];
            if (!pins.Contains(key, StringComparer.Ordinal)) next.Values[key] = values[i];
        }
    }
    private static double[] Interpolate(double[][] points, double position)
    {
        var coordinate = (position + 1) * 2;
        var segment = Math.Min(3, (int)Math.Floor(coordinate)); var weight = coordinate - segment;
        return Enumerable.Range(0, 3).Select(i => i == 2
            ? Math.Round(points[segment][i] + weight * (points[segment + 1][i] - points[segment][i]), MidpointRounding.AwayFromZero)
            : Math.Round(points[segment][i] + weight * (points[segment + 1][i] - points[segment][i]), 6)).ToArray();
    }
    private static (double Position, bool Approximate) Project(MusicExpertSettings settings, string prefix, double[][] points)
    {
        var values = Suffixes.Select(s => settings.Get(prefix + "." + s)).ToArray();
        var scale = Enumerable.Range(0, 3).Select(i => points[^1][i] - points[0][i]).ToArray();
        var best = double.PositiveInfinity; var position = 0d;
        // Project onto each normalized segment; rounded candidate counts have half-token display tolerance.
        for (var segment = 0; segment < 4; segment++) {
            var delta = Enumerable.Range(0, 3).Select(i => (points[segment + 1][i] - points[segment][i]) / scale[i]).ToArray();
            var dot = Enumerable.Range(0, 3).Sum(i => (values[i] - points[segment][i]) / scale[i] * delta[i]);
            var weight = Math.Clamp(dot / delta.Sum(v => v * v), 0, 1);
            var distance = Enumerable.Range(0, 3).Sum(i => Math.Pow((values[i] - points[segment][i]) / scale[i] - weight * delta[i], 2));
            if (distance < best) { best = distance; position = -1 + (segment + weight) / 2; }
        }
        var state = settings.Tuning; var intent = prefix == "abc_sampling" ? state?.X : state?.Y;
        if (intent is { } saved && Matches(Interpolate(points, saved), values)) return (saved, false);
        var projected = Interpolate(points, position);
        return (position, !Matches(projected, values));
        static bool Matches(double[] candidate, double[] actual) => Enumerable.Range(0, 3).All(i =>
            Math.Abs(candidate[i] - actual[i]) <= (i == 2 ? 0 : .000001));
    }
}
