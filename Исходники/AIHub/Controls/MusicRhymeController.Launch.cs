using System.Windows;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Controls;

/// <summary>Focus-triggered CPU checks and explicit, cancellable model launches.</summary>
public sealed partial class MusicRhymeController
{
    private CancellationTokenSource? _runCancel;
    private Task _runFinished = Task.CompletedTask;
    private Task<MusicRhymeStep>? _programTask;
    private string? _checkingId, _checkingProject;
    private bool _launching, _restarting, _lastPaused;
    private int _launchEpoch;
    public string? LaunchError { get; private set; }
    public string CurrentSource => _view.Editor.Lyrics;
    internal Func<bool> ProgramAvailable { get; set; } = () => System.IO.File.Exists(
        System.IO.Path.Combine(MusicRhymeCorpus.DirectoryPath, "corpus.json"));
    internal Func<MusicRhymeStep, Func<MusicRhymeStep, MusicRhymeStep>, IProgress<RagBookProgress>, CancellationToken,
        BackgroundOperationState?, Task<MusicRhymeStep>>? WorkRunner { get; set; }
    internal Func<MusicRhymeStep, Func<MusicRhymeStep, MusicRhymeStep>, IProgress<RagBookProgress>, CancellationToken,
        Task<MusicRhymeStep>> ProgramRunner { get; set; } = (step, save, progress, token) =>
            MusicRhymeProgramWork.RunAsync(step, save, progress, token);

    public bool IsRunning(string project, string? id = null) =>
        _activeProject == project && (id is null || _active == id)
        || _checkingProject == project && (id is null || _checkingId == id);
    public bool IsPaused(string project, string? id = null)
    {
        if (_activeProject != project || _active is null || id is not null && _active != id) return false;
        var state = ApplicationBackgroundOperations.Current?.FindOperation(MusicRhymeWork.Kind);
        return state is { Phase: BackgroundOperationPhase.Paused or BackgroundOperationPhase.Waiting }
            && state.Project == project && state.Input.ValueKind == System.Text.Json.JsonValueKind.Object
            && state.Input.TryGetProperty("Id", out var requestId) && requestId.GetString() == _active;
    }

    private void EditingStarted(object? sender, EventArgs args) => _debounce.Stop();
    private void EditingFinished(object? sender, EventArgs args)
    {
        if (!_disposed && _options.AutoAnalyze) { _debounce.Stop(); _debounce.Start(); }
    }
    internal async Task CheckAfterEditingAsync()
    {
        if (_disposed || !_options.AutoAnalyze || _view.Editor.IsEditing
            || string.IsNullOrWhiteSpace(_view.Editor.Lyrics)) return;
        if (_options.SelectionOnly && _view.Editor.CaptureSelection().Length == 0) return;
        try { await PrepareProgramAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { ReportLaunchError(error); }
    }
    private MusicRhymeRequest CaptureRequest(bool requireModel)
    {
        var selection = _view.Editor.CaptureSelection();
        var scope = MusicRhymeAnalyzer.Scope(selection, _options.SelectionOnly);
        var choice = _storage is null ? null : MusicRhymePreferences.Choices(_storage).FirstOrDefault(c => c.Candidate.Id == _options.ModelId);
        if (requireModel && (_storage is null || _models is null)) throw new InvalidOperationException("Music.Rhyme.Unavailable");
        if (requireModel && choice?.Model is null) throw new InvalidOperationException("Music.Poetry.ModelMissing");
        var request = new MusicRhymeRequest(Guid.NewGuid().ToString("N"), _view.Projects.Current.Id,
            selection.Text, scope.Start, scope.Length, _options.ModelId, choice?.Model?.Path,
            _storage?.Models.Locations.FirstOrDefault()?.Path ?? "", _l("Music.Rhyme.Language")) {
                Tuning = _options.Tuning.Snapshot(), CreativeMode = _options.CreativeMode,
                UsePersonalLyricsLibrary = _options.UsePersonalLyricsLibrary };
        request.Validate(); return request;
    }
    private static bool SameInput(MusicRhymeRequest a, MusicRhymeRequest b) => a.ProjectId == b.ProjectId
        && a.Source == b.Source && a.Start == b.Start && a.Length == b.Length && a.ModelId == b.ModelId
        && a.InterfaceLanguage == b.InterfaceLanguage && a.CreativeMode == b.CreativeMode
        && a.UsePersonalLyricsLibrary == b.UsePersonalLyricsLibrary && a.Tuning.Equals(b.Tuning);
    private bool StillCurrent(MusicRhymeRequest request)
    {
        if (_disposed || _view.Projects.Current.Id != request.ProjectId || _view.Editor.Lyrics != request.Source) return false;
        if (request.ModelId != _options.ModelId || request.CreativeMode != _options.CreativeMode
            || request.UsePersonalLyricsLibrary != _options.UsePersonalLyricsLibrary || !request.Tuning.Equals(_options.Tuning)
            || request.InterfaceLanguage != _l("Music.Rhyme.Language")) return false;
        try
        {
            var scope = MusicRhymeAnalyzer.Scope(_view.Editor.CaptureSelection(), _options.SelectionOnly);
            return scope.Start == request.Start && scope.Length == request.Length;
        }
        catch { return false; }
    }
    private MusicRhymeStep NewStep(MusicRhymeRequest request)
    {
        // Replace only disposable automatic previews; generated or manually edited drafts stay in history.
        if (_drafts.TryGetValue(request.ProjectId, out var history))
            history.RemoveAll(s => s.AutomaticPreview && !s.GenerationRequested && !s.OriginalEdited && !s.ProgramEdited && !s.ModelEdited);
        if (HistoryFor(request.ProjectId).Count >= _options.Tuning.Integer("history.steps_limit"))
            throw new InvalidOperationException("Music.Rhyme.Settings.HistoryFull");
        return new() { Request = request, Original = request.Text };
    }
    private async Task<MusicRhymeStep?> PrepareProgramAsync()
    {
        if (!ProgramAvailable()) return null;
        var request = CaptureRequest(false);
        var existing = History.LastOrDefault(s => SameInput(s.Request, request));
        if (existing is not null)
        {
            _visual = existing.Request.Id;
            if (_checkingId == existing.Request.Id && _programTask is not null) return await _programTask;
            if (existing.ProgramReady || _active == existing.Request.Id)
            {
                _analysis = existing.Analysis; _view.Editor.SetRhymeAnalysis(_analysis, _options.Color);
                ButtonState(); Changed?.Invoke(); return existing;
            }
        }
        _analysisCancel?.Cancel();
        var step = NewStep(request) with { AutomaticPreview = true };
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _analysisCancel = cancel; _checkingId = request.Id; _checkingProject = request.ProjectId; _visual = request.Id;
        LaunchError = null; Accept(step); _pulse.Start(); ButtonState();
        var task = RunProgramAsync(step, cancel.Token); _programTask = task;
        try { return await task; }
        finally
        {
            if (_analysisCancel == cancel)
            {
                _analysisCancel = null; _checkingId = _checkingProject = null; _programTask = null;
                if (_active is null) _pulse.Stop(); ButtonState(); Changed?.Invoke();
            }
        }
    }
    private async Task<MusicRhymeStep> RunProgramAsync(MusicRhymeStep step, CancellationToken token)
    {
        try
        {
            return await ProgramRunner(step, next => _view.Dispatcher.Invoke(() => {
                token.ThrowIfCancellationRequested();
                if (!StillCurrent(step.Request)) throw new OperationCanceledException(token);
                return Accept(next);
            }), new InlineProgress<RagBookProgress>(_ => { }), token);
        }
        catch (Exception error)
        {
            if (!_disposed && HistoryFor(step.Request.ProjectId).FirstOrDefault(s => s.Request.Id == step.Request.Id) is { } current)
                Accept(current with { Stage = "Partial", Error = error is OperationCanceledException ? "Music.Rhyme.Stopped" : error.Message });
            throw;
        }
    }
    private async void Click()
    {
        if (_disposed) return;
        Open(); // Opening never waits for a dictionary, another operation, or the model.
        if (_launching || _restarting || _activeProject == _view.Projects.Current.Id) return;
        _launching = true; _debounce.Stop(); LaunchError = null;
        var epoch = _launchEpoch;
        try
        {
            var request = CaptureRequest(true);
            var step = History.LastOrDefault(s => SameInput(s.Request, request));
            if (step is { ModelReady: true } || step is { GenerationRequested: true, Stage: "Partial" }) { Open(step.Request.Id); return; }
            EnsureGenerationAvailable();
            if (step is not null && _checkingId == step.Request.Id && _programTask is not null)
            {
                Accept(step with { GenerationRequested = true }); Open(step.Request.Id);
                await _programTask;
                if (!StillCurrent(request) || _restarting || epoch != _launchEpoch) return;
                step = History.FirstOrDefault(s => s.Request.Id == step.Request.Id);
            }
            EnsureGenerationAvailable();
            step ??= NewStep(request);
            step = step with { Request = request with { Id = step.Request.Id }, GenerationRequested = true, Error = null,
                Stage = step.ProgramReady ? "Reference" : "Analysis" };
            Accept(step); Open(step.Request.Id); LaunchGeneration(step);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ReportLaunchError(error); }
        finally { _launching = false; Changed?.Invoke(); }
    }
    private void EnsureGenerationAvailable()
    {
        if (_active is not null) throw new InvalidOperationException("Music.Rhyme.Running");
        if (ApplicationBackgroundOperations.Current?.BlocksNewWork == true) throw new InvalidOperationException("Music.Poetry.OtherOperation");
    }
    private void LaunchGeneration(MusicRhymeStep step) => _ = RunGenerationHandledAsync(step);
    private async Task RunGenerationHandledAsync(MusicRhymeStep step)
    {
        try { await RunAsync(step, _lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!_disposed)
            {
                var current = HistoryFor(step.Request.ProjectId).FirstOrDefault(s => s.Request.Id == step.Request.Id) ?? step;
                Accept(current with { Stage = "Partial", Error = error.Message });
            }
            ReportLaunchError(error);
        }
    }
    public async Task RestartAsync(string project)
    {
        if (_disposed || _restarting) return;
        if (project != _view.Projects.Current.Id || _active is not null && _activeProject != project)
            throw new InvalidOperationException("Music.Rhyme.Running");
        _restarting = true;
        _launchEpoch++;
        try
        {
            _debounce.Stop(); _analysisCancel?.Cancel();
            var program = _programTask;
            if (program is not null) try { await program; } catch (OperationCanceledException) { } catch { /* Retain the failed preview. */ }
            var previousId = _active;
            var finished = _runFinished; _runCancel?.Cancel(); await finished;
            var background = ApplicationBackgroundOperations.Current;
            var pending = background?.FindOperation(MusicRhymeWork.Kind);
            if (previousId is not null && pending is { } && pending.Project == project
                && pending.Input.TryGetProperty("Id", out var inputId) && inputId.GetString() == previousId)
                background!.DiscardPending(pending.Id);
            if (_disposed || project != _view.Projects.Current.Id) return;
            EnsureGenerationAvailable();
            var step = NewStep(CaptureRequest(true)) with { GenerationRequested = true };
            LaunchError = null; Accept(step); Open(step.Request.Id); LaunchGeneration(step);
        }
        finally { _restarting = false; Changed?.Invoke(); }
    }
    private void ReportLaunchError(Exception error)
    {
        if (_disposed) return;
        LaunchError = error.Message; _view.Status.AppendLog(Error(error.Message)); Changed?.Invoke();
    }
}
