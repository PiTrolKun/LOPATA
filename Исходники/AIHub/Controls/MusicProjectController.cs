using System.IO;
using System.Windows;
using System.Windows.Automation;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;

namespace AIHub.Controls;

/// <summary>Submitted snapshots only. Does not run models or autosave typing.</summary>
public sealed class MusicProjectController
{
    private readonly MusicWorkspaceControl _view;
    private readonly MusicProjects _store;
    private readonly MusicGenerationJobs _jobs;
    private Func<string, string> _l = key => key;
    private MusicProjectSnapshot? _initial;
    private Dictionary<string, MusicExpertSettings> _modelSettings = new();
    private int _cursor = -1;
    public MusicProject Current { get; private set; }
    public bool Busy { get; private set; }
    public MusicHistoryMode Mode { get; private set; }
    public Button ManageButton { get; }
    public MusicHistoryControl History { get; }
    public MusicProjectController(MusicWorkspaceControl view, MusicProjects store, MusicGenerationJobs jobs)
    {
        _view = view; _store = store; _jobs = jobs; Current = store.CreateDraft();
        History = new(this);
        ManageButton = MusicAudioUi.IconButton("Projects", "M2,7 V22 H22 V6 H11 L8,2 H2 Z M7,11 H17 M7,16 H17", ShowManager);
        ManageButton.Width = ManageButton.Height = 42;
        AutomationProperties.SetAutomationId(ManageButton, "Music.Projects.Manage");
    }
    public string Name(MusicProject project) => project.Name.Length > 0 ? project.Name
        : string.Format(_l("Music.Projects.DefaultName"), project.Number, project.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss"));
    public void Localize(Func<string, string> localize)
    { _l = localize; MusicAudioUi.Label(ManageButton, _l("Music.Projects.Title")); History.Refresh(); }
    public MusicProjectSnapshot Capture()
    {
        var options = _view.Generation.Options;
        var expert = _view.Generation.ExpertSettings; _modelSettings[expert.Variation] = expert.Snapshot();
        return new() { Variation = expert.Variation, ModelRevision = MusicModelVariants.Revision(expert.Variation),
            ModelSettings = _modelSettings.ToDictionary(p => p.Key, p => p.Value.Snapshot()), Lyrics = _view.Editor.Lyrics, Title = options.Title, Artist = options.Artist, Comment = options.Comment,
            Variants = options.Variants, DurationSeconds = options.DurationSeconds, OutputFolder = _view.Tracks.OutputFolder,
            Expert = _view.Generation.ExpertSettings, Output = options.Output, Wishes = MusicWishSnapshot.Capture(_view.Wishes.State) };
    }
    public void RememberDefaults() => _initial ??= Capture().Snapshot();
    public void SetBusy(bool busy) { Busy = busy; History.Refresh(); }
    public void CycleMode() { if (Busy) return; Mode = (MusicHistoryMode)(((int)Mode + 1) % 3); History.Refresh(); }
    public bool CanNavigate(int delta) => !Busy && Mode != MusicHistoryMode.Off && _cursor + delta >= 0 && _cursor + delta < Current.Steps.Length;
    public void Navigate(int delta)
    {
        if (!CanNavigate(delta)) return;
        _cursor += delta; Apply(Current.Steps[_cursor].Snapshot, Mode == MusicHistoryMode.Text, Mode == MusicHistoryMode.Settings);
        History.Refresh();
    }
    private void Apply(MusicProjectSnapshot snapshot, bool lyrics, bool settings, bool restoreBank = false)
    {
        if (settings) {
            if (snapshot.Model != MusicExpertCatalog.Model || !MusicModelVariants.Supported(snapshot.Variation)
                || snapshot.ModelRevision != MusicModelVariants.Revision(snapshot.Variation)) throw new InvalidDataException("Unsupported music project model.");
            _modelSettings[_view.Generation.Variation] = _view.Generation.ExpertSettings;
            if (restoreBank) foreach (var pair in snapshot.ModelSettings) _modelSettings[pair.Key] = pair.Value.Snapshot();
            _modelSettings[snapshot.Variation] = snapshot.Expert.Snapshot();
            var active = MusicModelVariants.WorkspaceSettings(snapshot.Expert);
            _view.Generation.ConfigureVariation(active.Variation, active);
            _view.Generation.SetOptions(new(snapshot.Title, snapshot.Variants, snapshot.DurationSeconds) {
                Artist = snapshot.Artist, Comment = snapshot.Comment, Output = snapshot.Output });
            _view.Generation.SetExpertSettings(active, false);
            _view.Wishes.Apply(snapshot.Wishes.ToPreferences()); _view.Tracks.SetOutputFolder(snapshot.OutputFolder);
            _view.Session.ModelChanged();
        }
        if (lyrics) _view.Editor.Lyrics = snapshot.Lyrics;
    }
    public void Fix()
    {
        EnsureIdle(); Current = _store.Fix(Named(), Capture()); History.Refresh();
    }
    public void ApplyExample(MusicProjectSnapshot snapshot)
    {
        EnsureIdle(); snapshot.Validate();
        Apply(snapshot with { OutputFolder = _view.Tracks.OutputFolder }, true, true);
        History.Refresh();
    }
    public void Rename(string name) { EnsureIdle(); Current = _store.Rename(Current, name); History.Refresh(); }
    private MusicProject Named() => Current.Name.Length > 0 ? Current : Current with { Name = Name(Current) };
    public void New()
    {
        EnsureIdle(); RememberDefaults(); Current = _store.CreateDraft(); _cursor = -1; _modelSettings.Clear();
        _view.Player.Clear(); _view.Tracks.ClearTracks(); Apply(_initial!, true, true, true); History.Refresh();
    }
    public IReadOnlyList<MusicProject> List() => _store.List();
    public void Open(string id)
    {
        EnsureIdle(); var project = _store.Load(id); project.Saved.Validate();
        if (project.Saved.Model != MusicExpertCatalog.Model || !MusicModelVariants.Supported(project.Saved.Variation)
            || project.Saved.ModelRevision != MusicModelVariants.Revision(project.Saved.Variation)) throw new InvalidDataException("Unsupported music project model.");
        _modelSettings.Clear();
        Current = project; _cursor = project.Steps.Length - 1; _view.Player.Clear(); _view.Tracks.ClearTracks();
        Apply(project.Saved, true, true, true); LoadTracks(); History.Refresh();
    }
    private void LoadTracks()
    {
        foreach (var step in Current.Steps) {
            try {
                var job = _jobs.Load(step.JobId);
                foreach (var track in _jobs.Tracks(step.JobId)) _view.Tracks.AddTrack(track);
                var active = ApplicationBackgroundOperations.Current is { HasPending: true, State.Kind: MusicGenerationRunner.BackgroundKind } controller
                    && controller.State!.Input.TryGetProperty("JobId", out var savedId) && savedId.GetString() == step.JobId;
                if (job.Variants.All(v => v.Completed) && step.Outcome != MusicProjectOutcome.Completed)
                    Current = _store.SetOutcome(Current.Id, step.JobId, MusicProjectOutcome.Completed);
                else if (!active && step.Outcome is MusicProjectOutcome.Pending or MusicProjectOutcome.Running or MusicProjectOutcome.Paused)
                    Current = _store.SetOutcome(Current.Id, step.JobId, MusicProjectOutcome.Interrupted);
            }
            catch (Exception error) when (error is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) {
                _view.Status.AppendLog(_l("Music.Projects.ReadError") + " " + error.Message);
            }
        }
    }
    public MusicGenerationJob Record(MusicGenerationJob job, MusicProjectSnapshot snapshot)
    {
        Current = _store.AddRequest(Named(), job.Id, snapshot); _cursor = Current.Steps.Length - 1;
        var linked = job with { ProjectId = Current.Id, ProjectName = Name(Current), ProjectStep = Current.Steps.Single(s => s.JobId == job.Id).Number };
        try { _jobs.Save(linked); }
        catch (Exception error) { Current = _store.SetOutcome(Current.Id, job.Id, MusicProjectOutcome.Failed, error.Message); throw; }
        History.Refresh(); return linked;
    }
    public void SwitchModel(string variation, string? verifiedHardware = null)
    {
        if (!_view.Session.CanChangeModel) throw new InvalidOperationException(_l("Music.Projects.Busy"));
        if (!MusicModelVariants.Supported(variation)) throw new InvalidDataException("Unsupported model variation.");
        _modelSettings[_view.Generation.Variation] = _view.Generation.ExpertSettings;
        var previous = _view.Generation.ExpertSettings;
        try {
            _view.Generation.ConfigureVariation(variation, _modelSettings.GetValueOrDefault(variation));
            Current = _store.SaveWorkspace(Current, Capture()); _view.Session.ModelChanged(verifiedHardware); History.Refresh();
        }
        catch { _view.Generation.ConfigureVariation(previous.Variation, previous); _view.Session.ModelChanged(); throw; }
    }
    public MusicGenerationJob RestoreJob(MusicGenerationJob job)
    {
        // Repair only an exact submitted JobId link if a crash happened between the two durable writes.
        if (job.ProjectId is null) {
            var known = _store.List().Where(p => p.Steps.Any(s => s.JobId == job.Id)).ToArray();
            if (known.Length > 1) throw new InvalidDataException("Ambiguous music project request.");
            if (known.SingleOrDefault() is { } project) {
                job = job with { ProjectId = project.Id, ProjectName = Name(project), ProjectStep = project.Steps.Single(s => s.JobId == job.Id).Number };
                _jobs.Save(job);
            }
        }
        if (job.ProjectId is { } id) {
            Current = _store.Load(id); _cursor = Array.FindIndex(Current.Steps, s => s.JobId == job.Id);
            if (_cursor < 0) throw new InvalidDataException("Music project request link is missing.");
            _view.Player.Clear(); _view.Tracks.ClearTracks(); Apply(Current.Steps[_cursor].Snapshot, true, true);
            LoadTracks();
        }
        else Apply(MusicProjectSnapshot.FromJob(job), true, true);
        History.Refresh(); return job;
    }
    public void Outcome(MusicGenerationJob job, MusicProjectOutcome outcome, string message = "")
    {
        if (job.ProjectId is null) return;
        var updated = _store.SetOutcome(job.ProjectId, job.Id, outcome, message);
        if (Current.Id == updated.Id) { Current = updated; History.Refresh(); }
    }
    private void EnsureIdle() { if (Busy) throw new InvalidOperationException(_l("Music.Projects.Busy")); }
    private void ShowManager()
    {
        try { new MusicProjectsWindow(this, _l) { Owner = Window.GetWindow(_view) }.ShowDialog(); }
        catch (Exception error) { _view.Status.AppendLog(_l("Music.Projects.ReadError") + " " + error.Message); }
    }
    public string Text(string key) => _l("Music.Projects." + key);
    public string HistoryHint => Name(Current) + "\n" + string.Format(Text("Step"), _cursor + 1, Current.Steps.Length)
        + (_cursor >= 0 ? "\n" + Text(Current.Steps[_cursor].Outcome.ToString()) : "");
}
