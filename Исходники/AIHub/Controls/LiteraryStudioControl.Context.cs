using System.Windows;
using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class LiteraryStudioControl
{
    private readonly LiteraryContextIndicator _contextButton = new();
    private ILiteraryContextTools _contextTools = null!;
    private Window? _contextWindow;
    private StudioContextMeter? _contextMeter;
    private bool _contextRejected;
    private CancellationTokenSource? _contextCounting;
    private string _contextStamp = "";
    private DateTime _contextNextMeasure;

    private void ConfigureContextManagement(ILiteraryContextTools? tools)
    {
        _contextTools = tools ?? _runtime; SetContextMeter(null);
        _contextButton.Click += (_, _) => OpenContextManager();
        _timer.Tick += async (_, _) => await RefreshContextMeterAsync();
        Loaded += (_, _) => { _runtime.StudioContextMeasured += ContextMeasured; _contextStamp = ""; };
        Unloaded += (_, _) => { _runtime.StudioContextMeasured -= ContextMeasured; _contextCounting?.Cancel(); };
    }
    private void ContextMeasured(StudioContextMeter meter) => Dispatcher.BeginInvoke(() => SetContextMeter(meter));
    private void SetContextMeter(StudioContextMeter? meter)
    {
        _contextMeter = meter;
        _contextButton.Update(meter, LiteraryContextWindowParts.Meter(meter, _l), _l("Studio.Context.Title"));
    }
    private StudioRequest ContextRequest(LiteraryStudioState state)
    {
        var editor = _draft.Capture(state.ProjectId, _directory);
        var selection = state.Selection.Where(x => x.Key != "chapters/" + editor.ActiveId && x.Key != "rag/project/" + editor.ActiveId
            && !(x.Key.StartsWith("jelly/", StringComparison.Ordinal) && x.Key.EndsWith("/" + editor.ActiveId, StringComparison.Ordinal)))
            .ToDictionary(x => x.Key, x => new ParagraphSelection { Selected = x.Value.Selected, Comment = x.Value.Comment });
        var basic = new ParagraphRequest(state.Role, state.Input, editor, [], selection, state.RouteId, state.Session, true);
        var writer = StudioContextPlan.Writer(state); var prompts = LiteraryPromptSets.Resolve(state, state.Action);
        return new(basic, state.Action, StudioContextPlan.Conversation(state), state.Quotes.ToArray(), writer.Task,
            state.Action == "Continue" && !state.ContinueFromChat ? "" : writer.Target,
            state.Action == "Continue" ? [] : writer.Requirements, state.ContinueFromChat, prompts.Action, prompts.Role);
    }
    private static string RequestStamp(StudioRequest request) => LiteraryWorkIndex.Revision(ParagraphJson.Encode(request));
    private async Task RefreshContextMeterAsync()
    {
        if (IsWorking || _requests.IsBusy || _contextCounting is not null || DateTime.UtcNow < _contextNextMeasure) return;
        _contextNextMeasure = DateTime.UtcNow.AddSeconds(2);
        try
        {
            var request = ContextRequest(State); var stamp = RequestStamp(request);
            // Retry unknown measurements once a backend becomes available; otherwise avoid repeated tokenization.
            if (stamp == _contextStamp && _contextMeter is not null && _contextMeter.Capacity == _requests.ContextCapacity) return;
            using var source = new CancellationTokenSource(); _contextCounting = source;
            var meter = await _contextTools.MeasureStudioContextAsync(request, _l, source.Token);
            if (!source.IsCancellationRequested && !IsWorking && stamp == RequestStamp(ContextRequest(State)))
            { _contextStamp = stamp; SetContextMeter(meter); }
        }
        catch (OperationCanceledException) { }
        catch { SetContextMeter(null); } // An unavailable counter must not interfere with typing or hide the request's status.
        finally { _contextCounting = null; }
    }
    private void OpenContextManager()
    {
        if (IsWorking || _blocked() || _requests.IsBusy || !Save()) return;
        _contextCounting?.Cancel();
        var plan = StudioContextPlan.Capture(State);
        async Task<StudioContextMeter?> Measure(IReadOnlySet<string> ids, string? summary, CancellationToken token)
        {
            var candidate = ids.Count == 0 ? State : plan.Apply(State, ids, summary, StudioContextMethod.Manual, null, null, "");
            return await _contextTools.MeasureStudioContextAsync(ContextRequest(candidate), _l, token);
        }
        async Task<bool> Apply(IReadOnlySet<string> ids, string? summary, StudioContextMethod method, CancellationToken token)
        {
            plan.EnsureCurrent(State);
            var before = await Measure(new HashSet<string>(), null, token);
            var after = await Measure(ids, summary, token); token.ThrowIfCancellationRequested();
            var note = string.Format(_l("Studio.Context.Journal"), DateTimeOffset.Now.ToString("g"),
                _l("Studio.Context.Method." + method), before?.Input.ToString() ?? "?", after?.Input.ToString() ?? "?");
            var candidate = plan.Apply(State, ids, summary, method, before?.Input, after?.Input, note);
            // Save atomically before touching the live state. A conflict or failure keeps the original intact.
            _store.Save(candidate);
            State.Messages = candidate.Messages; State.WriterContext = candidate.WriterContext;
            _dirty = false; _contextRejected = false; _contextStamp = ""; ClearContextFailure(); SetContextMeter(after); Render();
            return true;
        }
        var window = new LiteraryContextManagerWindow(Window.GetWindow(this), _l, plan, _contextMeter, _contextRejected,
            Measure, (method, ratio, progress, token) => _contextTools.CompactStudioContextAsync(plan, method, ratio, progress, token), Apply);
        _contextWindow = window; Availability();
        try { window.ShowDialog(); }
        finally { _contextWindow = null; _contextStamp = ""; Availability(); }
    }
}
