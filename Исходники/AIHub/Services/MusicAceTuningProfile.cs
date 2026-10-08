namespace AIHub.Services;

/// <summary>Versioned engineering paths. They do not promise lyrical accuracy or musical quality.</summary>
public static class MusicAceTuningProfile
{
    public const string Id = "ACE15.XLTurbo4B.LM.Bubble";
    public static readonly string[] Fields = ["lm_temperature", "lm_top_p", "lm_cfg_scale"];
    private static readonly double[] Temperature = [.55, .70, .85, 1, 1.15];
    private static readonly double[] Probability = [.80, .85, .90, .95, .98];
    public static bool Enabled(MusicExpertSettings settings) => new[] { "thinking", "use_cot_metas", "use_cot_caption", "use_cot_language" }
        .Any(key => settings.Get(key) == 1);
    public static MusicExpertSettings Move(MusicExpertSettings current, double x, double y)
    {
        current.Validate(); CheckCoordinates(x, y); x = Math.Clamp(x, -1, 1); y = Math.Clamp(y, -1, 1);
        if (!Enabled(current)) return current.Snapshot();
        var next = current.Snapshot(); var state = MusicTuningProfile.State(current);
        Set(next, state, "lm_temperature", Interpolate(Temperature, x));
        Set(next, state, "lm_top_p", Interpolate(Probability, x));
        Set(next, state, "lm_cfg_scale", y + 2);
        next = next with { Tuning = state with { X = x, Y = y, SimpleModified = true, ExpertModified = true } };
        next.Validate(); return next;
    }
    public static ModelBubblePosition Position(MusicExpertSettings settings)
    {
        var (x, approximate) = Project(settings.Get("lm_temperature"), settings.Get("lm_top_p"));
        var y = Math.Clamp(settings.Get("lm_cfg_scale") - 2, -1, 1);
        return new(x, y, approximate || Math.Abs(y + 2 - settings.Get("lm_cfg_scale")) > .000001, Enabled(settings));
    }
    public static MusicExpertSettings Release(MusicExpertSettings current, string key)
    {
        var state = MusicTuningProfile.State(current); var position = Position(current);
        var next = current.Snapshot() with { Tuning = state with { Pins = state.Pins.Except([key]).ToArray(),
            SimpleModified = true, ExpertModified = true } };
        var x = state.X ?? position.X; var y = state.Y ?? position.Y;
        next.Values[key] = key switch {
            "lm_temperature" => Interpolate(Temperature, x), "lm_top_p" => Interpolate(Probability, x),
            "lm_cfg_scale" => y + 2,
            "dcw_scaler" => SoundValue(state.Sound?.X ?? 0, .05),
            "dcw_high_scaler" => SoundValue(state.Sound?.Y ?? 0, .02),
            _ => MusicAceCatalog.Defaults().Get(key)
        };
        next.Validate(); return next;
    }
    // Candidate secondary circle: no UI exposure before listening tests. Auto means enabled for XL upstream.
    public static MusicExpertSettings MoveSound(MusicExpertSettings current, double x, double y)
    {
        current.Validate(); CheckCoordinates(x, y); x = Math.Clamp(x, -1, 1); y = Math.Clamp(y, -1, 1);
        if (current.Get("dcw_enabled") == 0) return current.Snapshot();
        var next = current.Snapshot(); var state = MusicTuningProfile.State(current);
        var mode = current.TextValues["dcw_mode"];
        Set(next, state, "dcw_scaler", SoundValue(x, .05));
        if (mode == "double") Set(next, state, "dcw_high_scaler", SoundValue(y, .02));
        next = next with { Tuning = state with { Sound = new() { X = x, Y = y }, SimpleModified = true, ExpertModified = true } };
        next.Validate(); return next;
    }
    private static double SoundValue(double coordinate, double center) => coordinate < 0
        ? center * (coordinate + 1) : center + (.1 - center) * coordinate;
    private static void Set(MusicExpertSettings next, ModelTuningState state, string key, double value)
    { if (!state.Pins.Contains(key, StringComparer.Ordinal)) next.Values[key] = Math.Round(value, 6); }
    private static void CheckCoordinates(double x, double y)
    { if (!double.IsFinite(x) || !double.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(x)); }
    private static double Interpolate(double[] points, double position)
    {
        var t = (position + 1) * 2; var i = Math.Min(3, (int)Math.Floor(t));
        return points[i] + (points[i + 1] - points[i]) * (t - i);
    }
    private static (double X, bool Approximate) Project(double temperature, double probability)
    {
        var best = double.PositiveInfinity; var result = 0d;
        for (var i = 0; i < 4; i++) {
            var dt = (Temperature[i + 1] - Temperature[i]) / .6;
            var dp = (Probability[i + 1] - Probability[i]) / .18;
            var a = (temperature - Temperature[i]) / .6; var b = (probability - Probability[i]) / .18;
            var weight = Math.Clamp((a * dt + b * dp) / (dt * dt + dp * dp), 0, 1);
            var distance = Math.Pow(a - weight * dt, 2) + Math.Pow(b - weight * dp, 2);
            if (distance < best) { best = distance; result = -1 + (i + weight) / 2; }
        }
        return (result, Math.Abs(Interpolate(Temperature, result) - temperature) > .000001
            || Math.Abs(Interpolate(Probability, result) - probability) > .000001);
    }
}
