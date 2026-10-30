using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;

namespace AIHub.Controls;

internal sealed partial class VideoVaceWorkspaceControl
{
    private H3FluidProgress _fluid = new();
    private TextBlock _progressSummary = new();
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Stopwatch _activeClock = new();
    private double _elapsedBefore, _stepSeconds, _lastStepAt, _displayedElapsed;
    private int _passIndex, _passTotal, _itemIndex, _itemTotal, _step, _steps;
    private string _stage = "";
    private bool _finalizing;
    private bool _clockSeeded;
    private DateTimeOffset? _scheduled;
    private void ProgressState(bool active, bool pending, bool fresh)
    {
        _progressTimer.Tick -= ProgressTick; _progressTimer.Tick += ProgressTick;
        if (fresh)
        {
            _fluid.Value = 0; _passIndex = _passTotal = _itemIndex = _itemTotal = _step = _steps = 0;
            _elapsedBefore = _stepSeconds = _lastStepAt = _displayedElapsed = 0; _stage = ""; _finalizing = false; _clockSeeded = false; _activeClock.Reset();
        }
        _fluid.Active = active;
        if (active) { _activeClock.Start(); _progressTimer.Start(); DrawProgress(); }
        else { _activeClock.Stop(); _progressTimer.Stop(); if (pending) _progressSummary.Text = T("ProgressPaused"); }
    }
    internal void SetScheduled(DateTimeOffset? due)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => SetScheduled(due)); return; }
        _scheduled = due; RefreshControls();
        if (due is null) return;
        _fluid.Active = false; _activeClock.Stop(); _progressTimer.Stop();
        Notify(string.Format(CultureInfo.CurrentCulture, T("ScheduledStatus"), due.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)));
    }
    internal void GenerationProgress(string line)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => GenerationProgress(line)); return; }
        if (line.StartsWith("[VACE event] ", StringComparison.Ordinal))
        {
            try
            {
                using var document = JsonDocument.Parse(line[13..]); var data = document.RootElement;
                if (data.ValueKind != JsonValueKind.Object) return;
                var kind = data.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : "";
                int Number(string key, int fallback) => data.TryGetProperty(key, out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out var value) ? Math.Clamp(value, 0, 1000000) : fallback;
                // Seed a restored run once. Subsequent backend observations and ETA
                // recalculations cannot accelerate or rewind the visible clock.
                if (_generating && !_clockSeeded && data.TryGetProperty("elapsed_seconds", out var elapsed) && elapsed.ValueKind == JsonValueKind.Number && elapsed.TryGetDouble(out var seconds) && double.IsFinite(seconds) && seconds >= 0)
                { _elapsedBefore = Math.Min(seconds, 315360000); _clockSeeded = true; }
                if (kind == "pass")
                {
                    _passIndex = Number("index", _passIndex); _passTotal = Number("total", _passTotal);
                    _itemIndex = Number("item_index", _itemIndex); _itemTotal = Number("item_total", _itemTotal);
                    _step = _steps = 0; _lastStepAt = 0; _finalizing = false; _stage = "conditioning";
                }
                else if (kind == "stage" && data.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    _stage = text.GetString() ?? ""; _finalizing = _stage is "assembling" or "scaling-output" or "saving" or "finalizing";
                    if (_stage == "sampling") { _step = 0; _steps = S.Steps; _lastStepAt = 0; }
                }
                else if (kind == "progress" && _stage == "sampling")
                {
                    var value = Number("value", 0); var total = Number("total", 0);
                    if (value > _step && _step > 0 && _lastStepAt > 0)
                    {
                        var measured = (Elapsed - _lastStepAt) / (value - _step);
                        if (measured > 0) _stepSeconds = _stepSeconds > 0 ? .7 * _stepSeconds + .3 * measured : measured;
                    }
                    _step = value; _steps = total; _lastStepAt = Elapsed;
                }
                else if (kind == "retry")
                { _stepSeconds = _lastStepAt = 0; Notify(string.Format(CultureInfo.CurrentCulture, T("ProgressRetry"), Number("attempt", 1), Number("total", S.RetryCount))); return; }
                else if (kind == "gpu" && data.TryGetProperty("utilization", out var load) && load.ValueKind == JsonValueKind.Number && load.TryGetDouble(out var utilization) && double.IsFinite(utilization)) _fluid.GpuUtilization = Math.Clamp(utilization, 0, 100);
                else if (kind == "done" && data.TryGetProperty("complete", out var complete) && complete.ValueKind == JsonValueKind.True) _fluid.Value = 100;
                DrawProgress();
            }
            catch (JsonException) { /* The host retains the original log; malformed progress cannot stop playback or generation. */ }
            return;
        }
        if (line.StartsWith("[VACE] ", StringComparison.Ordinal))
        {
            var detail = line[7..]; var message = T("Stage." + detail);
            if (!message.StartsWith("VideoPrep.", StringComparison.Ordinal)) { _stage = detail; Notify(message); }
        }
        else if (line.StartsWith("[Prepare]", StringComparison.Ordinal) || line.StartsWith("[Download]", StringComparison.Ordinal)) Notify(T("PreparingRuntime"));
    }
    private double Elapsed => Math.Max(0, _elapsedBefore + _activeClock.Elapsed.TotalSeconds);
    private void ProgressTick(object? sender, EventArgs args)
    {
        if (!_generating) return;
        // ETA and GPU events may redraw more often than once per second. Only this
        // clock tick advances the visible seconds, including after pause/resume.
        _displayedElapsed = Math.Max(_displayedElapsed, Math.Floor(Elapsed)); DrawProgress();
    }
    private static string Duration(double seconds)
    {
        var span = TimeSpan.FromSeconds(double.IsFinite(seconds) ? Math.Clamp(seconds, 0, 315360000) : 0);
        return ((int)span.TotalHours).ToString(CultureInfo.CurrentCulture) + span.ToString(@"\:mm\:ss", CultureInfo.InvariantCulture);
    }
    private void DrawProgress()
    {
        var parts = new List<string>();
        if (_itemTotal > 0) parts.Add(string.Format(CultureInfo.CurrentCulture, T("ProgressPlan"), _itemIndex, _itemTotal));
        if (_passTotal > 0) parts.Add(string.Format(CultureInfo.CurrentCulture, T("ProgressPass"), _passIndex, _passTotal));
        if (!_finalizing && _steps > 0) parts.Add(string.Format(CultureInfo.CurrentCulture, T("ProgressStep"), _step, _steps));
        if (_stage.Length > 0)
        { var message = T("Stage." + _stage); Notify(message.StartsWith("VideoPrep.", StringComparison.Ordinal) ? T("Generating") : message); }
        if (_fluid.Value < 100 && _finalizing) _fluid.Value = Math.Max(_fluid.Value, 95);
        else if (_fluid.Value < 100 && _passTotal > 0)
        { var progress = (_passIndex - 1 + (_steps > 0 ? Math.Clamp(_step / (double)_steps, 0, 1) : 0)) / _passTotal; _fluid.Value = Math.Max(_fluid.Value, Math.Clamp(progress * 92, 0, 92)); }
        if (_generating || _pending || _displayedElapsed > 0)
        {
            parts.Add(T("ProgressElapsed") + " " + Duration(_displayedElapsed));
            if (_stage == "sampling" && _stepSeconds > 0 && _steps > 0 && _passTotal > 0)
            { var left = Math.Max(0, _steps - _step) + Math.Max(0, _passTotal - _passIndex) * _steps; parts.Add(T("ProgressRemainingSampling") + " ~" + Duration(left * _stepSeconds)); }
            else parts.Add(T("ProgressWaiting"));
        }
        _progressSummary.Text = _pending ? T("ProgressPaused") : string.Join(" · ", parts);
    }
}
