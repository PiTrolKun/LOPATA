using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AIHub.Services;
using AIHub.Services.VideoPreparation;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using MessageBox = System.Windows.MessageBox;

namespace AIHub.Controls;

public sealed partial class VideoPreparationControl
{
    public bool Next()
    {
        if (ConceptBusy || PendingConcept) return false;
        if (Session.Project.Form.Length == 0) return false;
        var current = Session.Project.CurrentStep;
        if (current is "finish" or "generator") return ContinueFromFinish();
        if (!FinishBeforeNext()) return false;
        if (current is "shots" or "shots_translation" && !ShotsReviewed()) return false;
        if (!ReferencesReady()) return false;
        if (current == "world" && !Session.Project.World!.Cards.Any(c => !string.IsNullOrWhiteSpace(c.Text.IdeaText)))
        { _statusKey = "WorldMissingDescription"; RefreshStatus(); return false; }
        if (current is "idea" or "development" && !HasIdeaForNext()) return false;
        if (current is "translation" or "development_translation" or "world_translation" && !HasTranslationForNext()) return false;
        if (!WorldReviewed()) return false;
        if (!DevelopmentReviewed()) return false;
        if (!Save()) return false;
        Session.Project.CurrentStep = VideoPreparationProject.Steps[Array.IndexOf(VideoPreparationProject.Steps, current) + 1];
        if (IsDevelopmentStep) VideoDevelopment.Ensure(Session.Project);
        if (IsWorldStep) VideoWorld.Ensure(Session.Project);
        if (Session.Project.CurrentStep == "storyboard") VideoStoryboard.Ensure(Session.Project);
        if (Session.Project.CurrentStep == "finish")
        { var g = Session.Project.Generator ??= new(); g.FinalReached = true; g.PreparationStep = "package"; }
        Session.Changed();
        if (!Checkpoint()) { Session.Project.CurrentStep = current; Session.Changed(); return false; }
        Render(); return true;
    }
    public bool BackStepIfNeeded()
    {
        if (_shotsBusy || _shotsStarting || _finishingBusy) return false;
        if (Session.Project.CurrentStep == "form") return false;
        var current = Session.Project.CurrentStep;
        Session.Project.CurrentStep = VideoPreparationProject.Steps[Math.Max(0, Array.IndexOf(VideoPreparationProject.Steps, current) - 1)];
        Session.Changed();
        if (!Checkpoint()) { Session.Project.CurrentStep = current; Session.Changed(); }
        Render(); return true;
    }
    public bool Checkpoint()
    {
        _autosave.Stop();
        try { Session.Checkpoint(); _statusKey = ""; RefreshStatus(); RefreshConceptHistory(); RefreshWorldHistory(); return true; }
        catch (Exception error) when (IsStorageError(error)) { ShowError(error); return false; }
    }
    public bool Save()
    {
        if (Session.Project.CurrentStep == "storyboard" && _boardInvalidDuration) { _statusKey = "BoardDurationInvalid"; RefreshStatus(); return false; }
        if (_shotsInvalidDuration) { _statusKey = "ShotsDurationInvalid"; RefreshStatus(); return false; }
        if (Session.ArchivePath is null) { MissingLocation(); return false; }
        return SaveTo(Session.ArchivePath);
    }
    private bool SaveTo(string path)
    {
        _autosave.Stop();
        try
        {
            Session.Save(path); ClearLocationError(); _statusKey = "";
            if (_locationInput is not null) _locationInput.Input.Text = Session.ArchivePath ?? "";
            RefreshStatus(); RefreshConceptHistory(); RefreshTranslationHistory(); RefreshWorldHistory();
            return true;
        }
        catch (Exception error) when (IsStorageError(error)) { ShowError(error); return false; }
    }
    private bool ChooseLocation()
    {
        if (ConceptBusy || PendingConcept) return false;
        var name = Session.Project.Title.Trim(); if (name.Length == 0) name = T("DefaultTitle");
        name = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (name.Length > 120) name = name[..120];
        var dialog = new SaveFileDialog { Title = T("ChooseLocation"), Filter = T("FileFilter"),
            DefaultExt = VideoPreparationArchive.Extension, AddExtension = true,
            FileName = Session.ArchivePath is { } current ? Path.GetFileName(current) : name + VideoPreparationArchive.Extension };
        if (Session.ArchivePath is { } path) dialog.InitialDirectory = Path.GetDirectoryName(path);
        var accepted = Window.GetWindow(this) is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        return accepted == true && SaveTo(dialog.FileName);
    }
    private void OpenProject(bool recovery)
    {
        if (ConceptBusy || PendingConcept) return;
        if (!ConfirmReplace()) return;
        if (recovery && Session.Recoverable().Length == 0) { _statusKey = "NoRecovery"; RefreshStatus(); return; }
        var dialog = new OpenFileDialog { Title = T(recovery ? "Recovery" : "Open"), Filter = T("FileFilter"), CheckFileExists = true };
        if (recovery) dialog.InitialDirectory = Session.RecoveryRoot;
        var accepted = Window.GetWindow(this) is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        if (accepted != true) return;
        try
        {
            LoadUserProject(dialog.FileName, recovery);
        }
        catch (Exception error) when (IsStorageError(error)) { ShowError(error); }
    }
    public bool OpenPath(string path)
    {
        if (string.Equals(Session.ArchivePath, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) return true;
        if (ConceptBusy || PendingConcept) return false;
        if (!ConfirmReplace()) return false;
        try
        {
            _autosave.Stop(); Session.Load(path); ClearLocationError(); _statusKey = ""; Render(); return true;
        }
        catch (Exception error) when (IsStorageError(error)) { ShowError(error); return false; }
    }
    public async Task<bool> OpenPathWhenReadyAsync(string path, CancellationToken token = default)
    {
        _openingProject = true;
        try
        {
            // A shell request may arrive after the initial warmup has already begun.
            // Only cancel pure warmup; never interrupt generation or replace its source.
            _warmCancel?.Cancel();
            while (_warming) await Task.Delay(25, token);
            token.ThrowIfCancellationRequested();
            if (ConceptBusy || PendingConcept || !ConfirmReplace()) return false;
            return LoadUserProject(path, false);
        }
        finally { _openingProject = false; ScheduleWarmup(); }
    }
    private void NewProject()
    {
        if (ConceptBusy || PendingConcept) return;
        if (!ConfirmReplace()) return;
        _autosave.Stop(); Session.New(); _conceptChoices = []; _lastConceptPhase = null;
        ClearLocationError(); _statusKey = ""; Render();
    }
    private bool ConfirmReplace()
    {
        if (!Session.HasContent || !Session.IsDirty && Session.ArchivePath is not null) return true;
        var owner = Window.GetWindow(this);
        var result = owner is null ? MessageBox.Show(T("SaveQuestion"), T("Caption"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question)
            : MessageBox.Show(owner, T("SaveQuestion"), T("Caption"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (result == MessageBoxResult.Cancel) return false;
        if (result == MessageBoxResult.No)
        {
            try { Session.Checkpoint(userArchive: false); return true; }
            catch (Exception error) when (IsStorageError(error)) { ShowError(error); return false; }
        }
        return Session.ArchivePath is null ? ChooseLocation() : Save();
    }
    public bool CanClose()
    {
        if (Session.ArchivePath is not null && !Checkpoint()) return false;
        return ConfirmReplace();
    }
    private void MissingLocation()
    {
        _locationError = true; _statusKey = "MissingLocation"; RefreshStatus();
        _locationZone.BorderBrush = new SolidColorBrush(Colors.IndianRed);
        _locationZone.BeginAnimation(OpacityProperty, new DoubleAnimation(.3, 1, TimeSpan.FromMilliseconds(230))
        { AutoReverse = true, RepeatBehavior = new RepeatBehavior(3), FillBehavior = FillBehavior.Stop });
        _locationZone.BringIntoView();
        SystemSounds.Hand.Play();
    }
    private void ClearLocationError()
    {
        _locationError = false; _locationZone.BeginAnimation(OpacityProperty, null);
        _locationZone.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "LineBrush");
    }
    private static bool IsStorageError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException
        or ArgumentException or NotSupportedException or System.Security.SecurityException;
    private void ShowError(Exception error)
    {
        OwnedProcessRegistry.Log("project_storage_failed", "VideoPreparation", detail: error.GetType().Name);
        _statusKey = error.Message.StartsWith("VideoPrep.", StringComparison.Ordinal) ? error.Message[10..] : "IoError";
        if (T(_statusKey).StartsWith("VideoPrep.", StringComparison.Ordinal)) _statusKey = "IoError";
        RefreshStatus();
    }
}
