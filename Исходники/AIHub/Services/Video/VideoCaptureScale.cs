namespace AIHub.Services;

/// <summary>Full quality preserves detail and drops late frames instead. Other settings
/// adapt only to sustained work pressure, then recover; callback gaps are not work cost.</summary>
public sealed class VideoCaptureScale
{
    private readonly int _requested;
    private readonly bool _preserveDetail;
    public int Percent { get; private set; }
    public VideoCaptureScale(int requested, bool preserveDetail)
    { _requested = requested; _preserveDetail = preserveDetail; Percent = requested; }
    private double? _slowSince, _fastSince;
    public void Observe(double now, double cost, double interval, bool backPressure)
    {
        if (_preserveDetail) return;
        var slow = cost > interval * 0.8 || backPressure;
        var fast = cost < interval * 0.35 && !backPressure;
        _slowSince = slow ? _slowSince ?? now : null;
        _fastSince = fast ? _fastSince ?? now : null;
        if (_slowSince is { } slowStart && now - slowStart >= 1000 && Percent > Math.Max(1, _requested / 4))
        { Percent = Math.Max(Math.Max(1, _requested / 4), Percent / 2); _slowSince = _fastSince = null; }
        else if (_fastSince is { } fastStart && now - fastStart >= 5000 && Percent < _requested)
        { Percent = Math.Min(_requested, Percent * 2); _slowSince = _fastSince = null; }
    }
}
