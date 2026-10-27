using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VideoPreparation;

namespace AIHub.Controls;

public sealed partial class VideoPreparationControl
{
    private bool _shotsBusy, _shotsStarting, _shotsScheduled, _shotsInvalidDuration;
    private CancellationTokenSource? _shotsCancel;
    private TextBlock? _shotsStageText;
    private System.Windows.Controls.TextBox? _shotsThoughtBox;
    private string _shotsStage = "ShotsWorking", _shotsThoughts = "";
    private int _shotsIndex, _shotsTotal;
    private VideoShotOptionsWindow? _shotOptions;
    private void ScheduleShots()
    {
        if (_shotsScheduled || _shotsStarting || _shotsBusy || _assistant is null || !IsLoaded || !IsVisible
            || Session.Project.CurrentStep != "shots" || Session.Project.Shots?.Attempted == true || ConceptBusy || PendingConcept) return;
        _shotsScheduled = true;
        Dispatcher.BeginInvoke(async () =>
        {
            _shotsScheduled = false;
            if (IsVisible && Session.Project.CurrentStep == "shots" && Session.Project.Shots?.Attempted != true)
                await StartShotsAsync(automatic: true);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }
    public async Task StartShotsAsync(bool automatic = false, bool translation = false, bool alternatives = false,
        string editShotId = "", int freedom = 1, CancellationToken token = default)
    {
        if (ConceptBusy || PendingConcept || _shotsStarting || _assistant is null) return;
        var p = Session.Project; var d = VideoShots.Ensure(p);
        if (automatic && d.Attempted) return;
        _drawingStage = false; _warmDeferred = false;
        d.Attempted = true; Session.Changed();
        _shotsStarting = true;
        using var preparation = CancellationTokenSource.CreateLinkedTokenSource(token, ApplicationBackgroundOperations.ExitToken);
        _shotsCancel = preparation; _shotsThoughts = ""; Render();
        try
        {
            if (!Checkpoint()) return;
            if (_conceptChoices.Count == 0 && _loadConceptChoices is not null) _conceptChoices = await _loadConceptChoices(preparation.Token);
            if (!ReferenceEquals(p, Session.Project)) return;
            if (d.ModelId == "") d.ModelId = p.LastAssistantModelId.Length > 0 ? p.LastAssistantModelId : VideoConceptModels.Default(_conceptChoices);
            var choice = _conceptChoices.FirstOrDefault(c => c.Id == d.ModelId);
            if (choice is not { Model: not null, Fits: not false }) { _statusKey = "AiNeedsPreparation"; return; }
            p.LastAssistantModelId = d.ModelId; VideoShots.Capture(p);
            if (translation && !VideoShots.Ready(p)) { _statusKey = "ShotsMissing"; return; }
            var source = VideoShots.Source(p); var ids = new List<string>();
            var identity = VideoShots.Hash(choice.Id + "\n" + choice.Model.Path + "\n" + EngineSetSelection.ActiveId + "\n" + InputLanguage);
            foreach (var level in alternatives ? new[] { 1, 2, 3 } : new[] { freedom })
            {
                var c = !translation && !alternatives && editShotId == "" ? d.Candidates.LastOrDefault(c => !c.Complete && c.Automatic
                    && c.Source == VideoShots.Hash(source) && c.BaseSnapshot == VideoShots.Snapshot(d.Scenes) && c.Freedom == level
                    && c.RequestIdentity == identity) : null;
                if (c is null) { c = new VideoShotCandidate { Freedom = level, Source = VideoShots.Hash(source), BaseSnapshot = VideoShots.Snapshot(d.Scenes), RequestIdentity = identity, Automatic = !alternatives && !translation }; d.Candidates.Add(c); }
                ids.Add(c.Id);
            }
            Session.Changed(); if (!Save()) return;
            var job = new VideoShotsJob(Guid.NewGuid().ToString("N"), p.Id, Session.ArchivePath!, source, p.Form, choice.Id,
                choice.Model.Path, EngineSetSelection.ActiveId, InputLanguage, ids.ToArray(), translation, editShotId);
            if (alternatives)
            {
                OpenShotOptions(ids);
            }
            _shotsStarting = false; await RunShotsJobAsync(job, preparation.Token);
        }
        catch (OperationCanceledException) { _statusKey = "AiCanceled"; }
        catch (Exception e) { ConceptError(e); }
        finally { _shotsStarting = false; if (!_shotsBusy) _shotsCancel = null; Render(); }
    }
    private async Task RunShotsJobAsync(VideoShotsJob job, CancellationToken token, BackgroundOperationState? restored = null)
    {
        if (_assistant is null) throw new InvalidOperationException("VideoPrep.AiUnavailable");
        var p = Session.Project; _shotsBusy = true;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token, ApplicationBackgroundOperations.ExitToken); _shotsCancel = cancel;
        _shotsStage = job.Translation ? "ShotsTranslating" : "ShotsWorking"; _shotsThoughts = ""; Render();
        try
        {
            await VideoShotsWork.RunAsync(job, p, _assistant, _conceptReceipts, async () =>
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!ReferenceEquals(p, Session.Project)) throw new InvalidDataException("VideoPrep.AiWrongProject");
                    Session.Changed(); if (!Checkpoint()) throw new IOException("VideoPrep.IoError");
                    _shotOptions?.Update(p.Shots!.Candidates.Where(c => job.CandidateIds.Contains(c.Id)));
                });
            }, (stage, index, total) => Dispatcher.BeginInvoke(() =>
            { _shotsStage = stage; _shotsIndex = index; _shotsTotal = total; UpdateShotsProgress(); }),
                thought => Dispatcher.BeginInvoke(() =>
                {
                    if (_shotsThoughts.Length < 32000) _shotsThoughts += thought[..Math.Min(thought.Length, 32000 - _shotsThoughts.Length)];
                    if (_shotsThoughtBox is not null) { _shotsThoughtBox.Text = _shotsThoughts; _shotsThoughtBox.ScrollToEnd(); }
                }), cancel.Token, restored, T(job.Translation ? "ShotsTranslating" : "ShotsWorking"));
            _statusKey = job.Translation ? "ShotsTranslated" : "ShotsReady";
        }
        catch (OperationCanceledException) { _statusKey = "AiCanceled"; }
        catch (Exception e) { ConceptError(e); }
        finally { _shotsBusy = false; _shotsCancel = null; Render(); }
    }
    private void UpdateShotsProgress()
    { if (_shotsStageText is not null) _shotsStageText.Text = (PendingConceptState?.Phase is BackgroundOperationPhase.Paused or BackgroundOperationPhase.Waiting ? T("AiPaused") + " · " : "")
            + T(_shotsStage) + (_shotsTotal > 0 ? $" {_shotsIndex}/{_shotsTotal}" : ""); RefreshStatus(); }
    private async Task ShotsModelAsync(bool improve = false, string shotId = "")
    {
        try
        {
        if (ConceptBusy || PendingConcept) return;
        if (_conceptChoices.Count == 0 && _loadConceptChoices is not null) _conceptChoices = await _loadConceptChoices(ApplicationBackgroundOperations.ExitToken);
        var d = VideoShots.Ensure(Session.Project);
        var window = new VideoConceptOptionsWindow(Window.GetWindow(this), _localize, _conceptChoices,
            d.ModelId == "" ? Session.Project.LastAssistantModelId : d.ModelId, 1);
        if (BackgroundUserDecision.ShowDialog(window, this, ApplicationBackgroundOperations.ExitToken) != true) return;
        d.ModelId = window.SelectedModelId; Session.Project.LastAssistantModelId = d.ModelId; Changed(); Checkpoint(); Render();
        if (improve) await StartShotsAsync(editShotId: shotId, freedom: window.Freedom);
        else ScheduleWarmup();
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { ConceptError(e); RefreshStatus(); }
    }
    private void OpenShotOptions(IEnumerable<string> ids)
    {
        var p = Session.Project; var d = VideoShots.Ensure(p); var chosenIds = ids.ToHashSet(); _shotOptions?.Close();
        _shotOptions = new(Window.GetWindow(this), _localize, id =>
        {
            if (!ReferenceEquals(p, Session.Project)) return;
            if (VideoShots.Select(p, d.Candidates.Single(c => c.Id == id)))
            { Session.Changed(); if (Checkpoint()) { _shotsCancel?.Cancel(); _shotOptions?.Close(); Render(); } }
        });
        _shotOptions.Closed += (_, _) => { _shotOptions = null; _shotsCancel?.Cancel(); };
        _shotOptions.Update(d.Candidates.Where(c => chosenIds.Contains(c.Id))); _shotOptions.Show();
    }
    private bool ShotsReviewed()
    {
        if (_shotsInvalidDuration) { _statusKey = "ShotsDurationInvalid"; RefreshStatus(); return false; }
        if (!VideoShots.Ready(Session.Project)) { _statusKey = "ShotsMissing"; RefreshStatus(); return false; }
        var p = Session.Project; var d = p.Shots!;
        var translation = p.CurrentStep == "shots_translation";
        if (translation && d.Scenes.Any(s => !VideoShots.SameStructure(s, VideoShots.SelectedTranslation(d, s.Id)?.Scene))) { _statusKey = "ShotsMissingTranslation"; RefreshStatus(); return false; }
        if (VideoShots.Current(p) && (!translation || d.Scenes.All(s => VideoShots.TranslationCurrent(d, s)))) return true;
        var snapshot = VideoShots.Snapshot(d.Scenes); var source = VideoShots.Source(p);
        var dialog = new VideoPreparationDecisionWindow(Window.GetWindow(this), _localize, "ReviewTitle", T("ShotsReview"), ("ReviewContinue", "continue"), ("ReviewEdit", "cancel"));
        if (BackgroundUserDecision.ShowDialog(dialog, this, ApplicationBackgroundOperations.ExitToken) != true || dialog.Choice != "continue") return false;
        if (snapshot != VideoShots.Snapshot(d.Scenes) || source != VideoShots.Source(p)) return false;
        d.Source = VideoShots.Hash(source);
        if (translation) foreach (var scene in d.Scenes) VideoShots.SelectedTranslation(d, scene.Id)!.NativeSnapshot = VideoShots.Snapshot([scene]);
        Session.Changed(); return Checkpoint();
    }
    public async Task ResumeShotsAsync(BackgroundOperationState state, CancellationToken token)
    {
        var job = state.Input.Deserialize<VideoShotsJob>() ?? throw new InvalidDataException("VideoPrep.AiInvalidRequest"); VideoShotsWork.Validate(job);
        if (!OpenPath(job.ProjectPath) || Session.Project.Id != job.ProjectId) throw new BackgroundOperationWaitingException("VideoPrep.AiWrongProject");
        Session.Project.CurrentStep = job.Translation ? "shots_translation" : "shots"; await RunShotsJobAsync(job, token, state);
    }
    public bool ShowShotsResult(BackgroundOperationState state)
    {
        var job = state.Input.Deserialize<VideoShotsJob>() ?? throw new InvalidDataException("VideoPrep.AiInvalidRequest"); VideoShotsWork.Validate(job);
        if (ConceptBusy || !OpenPath(job.ProjectPath) || Session.Project.Id != job.ProjectId) return false;
        Session.Project.CurrentStep = job.Translation ? "shots_translation" : "shots"; Render(); return true;
    }
}
