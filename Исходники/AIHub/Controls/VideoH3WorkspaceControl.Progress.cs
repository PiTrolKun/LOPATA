using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace AIHub.Controls;

internal sealed partial class VideoH3WorkspaceControl
{
    private H3FluidProgress _fluid = new();
    private TextBlock _progressSummary = new();
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Stopwatch _activeClock = new();
    private double _elapsedBefore, _stepSeconds, _displayedElapsed;
    private int _passIndex, _passTotal, _itemIndex, _itemTotal, _step, _steps;
    private int _refinementIndex, _refinementTotal, _refinementStep, _refinementSteps;
    private bool _finalizing;
    private string _stage = "";
    private DateTimeOffset? _scheduled;
    private double _lastStepAt;
    private void ProgressState(bool active, bool pending)
    {
        _fluid.Active = active;
        _progressTimer.Tick -= ProgressTick; _progressTimer.Tick += ProgressTick;
        if (active)
        {
            if (_fluid.Value >= 100)
            {
                _fluid.Value = 0; _passIndex = _passTotal = _itemIndex = _itemTotal = _step = _steps = 0;
                _refinementIndex = _refinementTotal = _refinementStep = _refinementSteps = 0; _finalizing = false;
                _elapsedBefore = _stepSeconds = _lastStepAt = _displayedElapsed = 0; _activeClock.Reset(); _stage = "";
            }
            _scheduled = null; _activeClock.Start(); _progressTimer.Start();
        }
        else
        {
            _activeClock.Stop(); _progressTimer.Stop();
            if (pending) _progressSummary.Text = T("H3ProgressPaused");
        }
        if (active) DrawProgress();
    }
    internal void SetScheduled(DateTimeOffset? due)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => SetScheduled(due)); return; }
        _scheduled = due;
        if (due is null) return;
        _externalBusy = true; _pendingH3 = true; _generating = false;
        _settings.IsEnabled = false; _files.IsEnabled = false; _generate.IsEnabled = false; _navigation.IsEnabled = false;
        _cancelExport.Visibility = Visibility.Visible; _pause.Visibility = Visibility.Collapsed;
        _fluid.Active = false; _activeClock.Stop(); _progressTimer.Stop();
        Notify(string.Format(CultureInfo.CurrentCulture, T("H3ScheduledStatus"), due.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)));
    }
    internal void GenerationProgress(string line)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => GenerationProgress(line)); return; }
        if (line.StartsWith("[H3 event] ", StringComparison.Ordinal))
        {
            try
            {
                using var document = JsonDocument.Parse(line[11..]); var data = document.RootElement;
                var kind = data.TryGetProperty("kind", out var k) ? k.GetString() : "";
                int Number(string key, int fallback) => data.TryGetProperty(key, out var n) && n.TryGetInt32(out var value) ? value : fallback;
                if (data.TryGetProperty("elapsed_seconds", out var elapsed) && elapsed.TryGetDouble(out var seconds) && double.IsFinite(seconds) && seconds >= 0)
                { _elapsedBefore = seconds - _activeClock.Elapsed.TotalSeconds; }
                if (kind == "pass")
                {
                    _finalizing = false;
                    _passIndex = Number("index", _passIndex); _passTotal = Number("total", _passTotal);
                    _itemIndex = Number("item_index", _itemIndex); _itemTotal = Number("item_total", _itemTotal);
                    _step = 0; _steps = 0; _lastStepAt = Elapsed; _stage = "conditioning";
                }
                else if (kind == "progress")
                {
                    var value = Number("value", 0); var total = Number("total", 0);
                    if (_stage == "sampling" && value > _step && _step > 0 && _lastStepAt > 0)
                    {
                        var measured = (Elapsed - _lastStepAt) / (value - _step);
                        if (measured > 0) _stepSeconds = _stepSeconds > 0 ? .7 * _stepSeconds + .3 * measured : measured;
                    }
                    if (_stage == "sampling") { _step = value; _steps = total; _lastStepAt = Elapsed; }
                    else if (_stage == "refinement-sampling")
                    { _refinementStep = value; _refinementSteps = total; }
                }
                else if (kind == "stage" && data.TryGetProperty("text", out var text))
                {
                    _stage = text.GetString() ?? "";
                    if (_stage is "assembling" or "upscaling" or "refinement-sampling" or "refinement-rendering" or "scaling-output") _finalizing = true;
                    if (_stage == "sampling") { _step = 0; _steps = S.Steps; _lastStepAt = 0; }
                    else if (_stage == "refinement-sampling") { _refinementStep = 0; _refinementSteps = S.RefineSteps; }
                }
                else if (kind == "refinement-segments")
                {
                    _finalizing = true; _stage = "upscaling";
                    _refinementTotal = Number("total", 0); _refinementIndex = _refinementStep = _refinementSteps = 0;
                }
                else if (kind == "refinement")
                {
                    _finalizing = true; _stage = "upscaling";
                    _refinementIndex = Number("index", 0); _refinementTotal = Number("total", _refinementTotal);
                    _refinementStep = _refinementSteps = 0;
                }
                else if (kind == "retry")
                {
                    _stepSeconds = 0; _lastStepAt = 0;
                    Notify(string.Format(CultureInfo.CurrentCulture, T("H3ProgressRetry"), Number("attempt", 1), Number("total", S.RetryCount)));
                    return;
                }
                else if (kind == "gpu" && data.TryGetProperty("utilization", out var load) && load.TryGetDouble(out var utilization) && double.IsFinite(utilization))
                    _fluid.GpuUtilization = Math.Clamp(utilization, 0, 100);
                else if (kind == "done" && data.TryGetProperty("complete", out var complete) && complete.ValueKind == JsonValueKind.True) _fluid.Value = 100;
                DrawProgress();
            }
            catch (JsonException) { /* Human-readable log remains available through the host. */ }
            return;
        }
        if (line.StartsWith("[H3] ", StringComparison.Ordinal))
        {
            var detail = line[5..]; var key = "H3Stage." + detail;
            if (T(key) != "VideoPrep." + key) { _stage = detail; Notify(T(key)); }
            else if (!detail.Contains('/')) Notify(T("H3Generating") + " · " + detail);
        }
        else if (line.StartsWith("[Prepare]", StringComparison.Ordinal) || line.StartsWith("[Download]", StringComparison.Ordinal)) Notify(T("H3PreparingRuntime"));
    }
    private double Elapsed => _elapsedBefore + _activeClock.Elapsed.TotalSeconds;
    private void ProgressTick(object? sender, EventArgs e)
    {
        // Progress/ETA/GPU events may redraw between timer ticks. Keep the visible
        // seconds on one cadence while retaining the authoritative time for ETA.
        _displayedElapsed = Math.Max(_displayedElapsed, Math.Floor(Elapsed));
        DrawProgress();
    }
    private static string Duration(double seconds) => TimeSpan.FromSeconds(Math.Max(0, Math.Min(seconds, 315360000))) is var span
        ? ((int)span.TotalHours).ToString(CultureInfo.CurrentCulture) + span.ToString(@"\:mm\:ss", CultureInfo.InvariantCulture) : "";
    private void DrawProgress()
    {
        var parts = new List<string>();
        if (_finalizing)
        {
            if (_refinementTotal > 0 && _refinementIndex > 0)
                parts.Add(string.Format(CultureInfo.CurrentCulture, T("H3ProgressRefinement"), _refinementIndex, _refinementTotal));
            if (_stage == "refinement-sampling" && _refinementSteps > 0)
                parts.Add(string.Format(CultureInfo.CurrentCulture, T("H3ProgressStep"), _refinementStep, _refinementSteps));
        }
        else
        {
            if (_itemTotal > 0) parts.Add(string.Format(CultureInfo.CurrentCulture, T("H3ProgressPlan"), _itemIndex, _itemTotal));
            if (_passTotal > 0) parts.Add(string.Format(CultureInfo.CurrentCulture, T("H3ProgressPass"), _passIndex, _passTotal));
            if (_steps > 0) parts.Add(string.Format(CultureInfo.CurrentCulture, T("H3ProgressStep"), _step, _steps));
        }
        if (_stage.Length > 0)
        {
            var stage = T("H3Stage." + _stage);
            Notify(stage.StartsWith("VideoPrep.", StringComparison.Ordinal) ? T("H3Generating") : stage);
        }
        if (_fluid.Value < 100 && _finalizing)
        {
            var portion = _refinementSteps > 0 ? Math.Clamp(_refinementStep / (double)_refinementSteps, 0, 1) : 0;
            var progress = _refinementTotal > 0 && _refinementIndex > 0 ? (_refinementIndex - 1 + portion) / _refinementTotal : 0;
            _fluid.Value = Math.Max(_fluid.Value, Math.Clamp(93 + progress * 6, 93, 99));
        }
        else if (_fluid.Value < 100 && _passTotal > 0)
        {
            var progress = (_passIndex - 1 + (_steps > 0 ? Math.Clamp(_step / (double)_steps, 0, .98) : 0)) / _passTotal;
            _fluid.Value = Math.Max(_fluid.Value, Math.Clamp(progress * 92, 0, 92));
        }
        parts.Add(T("H3ProgressElapsed") + " " + Duration(_displayedElapsed));
        if (_stage == "sampling" && _stepSeconds > 0 && _steps > 0 && _passTotal > 0)
        {
            var remainingSteps = Math.Max(0, _steps - _step) + Math.Max(0, _passTotal - _passIndex) * _steps;
            parts.Add(T("H3ProgressRemainingSampling") + " ~" + Duration(remainingSteps * _stepSeconds));
        }
        else parts.Add(T("H3ProgressWaiting"));
        _progressSummary.Text = string.Join(" · ", parts);
    }
}
