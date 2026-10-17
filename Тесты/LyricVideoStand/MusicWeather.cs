namespace LyricVideoStand;

internal readonly record struct WeatherState(float Storm, double RainTravel, double WaveTravel)
{
    public float Density => .09f + .91f * Storm;
    public float WaveHeight => 3 + 160 * Storm;
    public float Wind => .02f + Storm * .56f;
}

/// <summary>Song-owned, integrated motion: changing intensity never teleports streams.
/// All values are precomputed, so seeking and offline rendering use exactly the same weather.</summary>
internal sealed class MusicWeather
{
    private readonly double[] _times;
    private readonly WeatherState[] _values;
    public MusicWeather(AudioAnalysis analysis)
    {
        _times = analysis.Points.Select(p => p.Time).ToArray(); _values = new WeatherState[_times.Length];
        float storm = 0; double rain = 0, waves = 0, previous = 0;
        for (var i = 0; i < _values.Length; i++)
        {
            var a = analysis.Points[i].Energy;
            var strength = Math.Clamp((a.Level * .82f + a.Bass * .18f - .18f) / .70f, 0, 1);
            var target = MathF.Pow(strength * strength * (3 - 2 * strength), 1.25f);
            var dt = Math.Max(0, _times[i] - previous);
            var blend = 1 - Math.Exp(-dt / (target > storm ? .10 : .52));
            storm += (target - storm) * (float)blend;
            rain += (42 + storm * 430) * dt;
            waves += (.38 + storm * 2.6) * dt;
            _values[i] = new(storm, rain, waves); previous = _times[i];
        }
    }
    public WeatherState At(double time)
    {
        if (!double.IsFinite(time) || time <= 0 || _times.Length == 0) return default;
        var index = Array.BinarySearch(_times, time);
        if (index >= 0) return _values[index];
        index = ~index;
        if (index == 0)
        {
            var v = _values[0]; var fraction = (float)(time / _times[0]);
            return new(v.Storm * fraction, v.RainTravel * fraction, v.WaveTravel * fraction);
        }
        if (index >= _times.Length)
        {
            var v = _values[^1]; var extra = time - _times[^1];
            return new(v.Storm * (float)Math.Exp(-extra / .52), v.RainTravel + extra * 42, v.WaveTravel + extra * .38);
        }
        var a = _values[index - 1]; var b = _values[index];
        var t = (time - _times[index - 1]) / (_times[index] - _times[index - 1]);
        return new(a.Storm + (b.Storm - a.Storm) * (float)t, a.RainTravel + (b.RainTravel - a.RainTravel) * t,
            a.WaveTravel + (b.WaveTravel - a.WaveTravel) * t);
    }
}
