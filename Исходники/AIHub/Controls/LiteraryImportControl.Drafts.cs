using System.IO;
using System.Windows;
using System.Windows.Threading;
using AIHub.Services.LiteraryImport;

namespace AIHub.Controls;

public sealed partial class LiteraryImportControl
{
    private readonly LiteraryImportDraftStore _draftStore;
    private readonly DispatcherTimer _draftSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private string _draftId = Guid.NewGuid().ToString("N");
    private bool _draftTouched, _restoringDraft, _draftFinished;
    private string _draftStep = "source";

    private void InitializeDraftSaving()
    {
        _draftSaveTimer.Tick += (_, _) => { _draftSaveTimer.Stop(); SaveDraft(); };
        foreach (var box in new[] { _draftFolder, _draftSourceFile, _draftProjectName, _draftWorkTitle })
            box.TextChanged += (_, _) => DraftChanged();
    }

    private void DraftChanged()
    {
        if (_restoringDraft || _draftFinished) return;
        _draftTouched = true;
        _draftSaveTimer.Stop(); _draftSaveTimer.Start();
    }

    private bool SaveDraft(string? step = null)
    {
        _draftSaveTimer.Stop();
        if (!_draftTouched || _draftFinished) return true;
        if (step is not null) _draftStep = step;
        try
        {
            var matchingSession = _session is not null && _parsedSessionId == _session.State.Id
                && string.Equals(_parsedSourcePath, _draftSourceFile.Text.Trim(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(_parsedFolderPath, _draftFolder.Text.Trim(), StringComparison.OrdinalIgnoreCase);
            _draftStore.Remember(new LiteraryImportDraft(_draftId, _draftFolder.Text.Trim(),
                _draftSourceFile.Text.Trim(), _draftProjectName.Text.Trim(), _draftWorkTitle.Text.Trim(),
                matchingSession ? _session!.Root : "", matchingSession ? _draftStep : "source",
                (_showingQuickPreview || _showingAnalysis || _showingWorkChoice || _showingBookReview) && _session is not null ? _session.State.Conversations :
                    _conversations.Where(c => c.Box.IsChecked == true).Select(c => c.Id).ToArray(), DateTimeOffset.Now)
            {
                SelectedUnitIds = _selectedPreviewUnits.ToArray(),
                SplitGroupKeys = _splitPreviewGroups.ToArray(),
                ConfirmedGroupKeys = _confirmedPreviewGroups.ToArray(),
                EditedGroupNames = new Dictionary<string, string>(_editedPreviewTitles, StringComparer.Ordinal),
                WorkSelectionKey = _workSelectionKey,
                SelectedWorkUnitIds = _selectedWorkUnits.Order(StringComparer.Ordinal).ToArray()
            });
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Text = L("RecentSaveFailed") + " " + ex.Message;
            _status.Visibility = Visibility.Visible;
            return false;
        }
    }

    public bool CheckpointBackgroundState()
    {
        _session?.Save();
        return (_postReviewQuestions?.TrySaveDraft() ?? true) & SaveAnswers() & SaveDraft();
    }

    public void ShowRecentDrafts()
    {
        if (IsBusy) return;
        if (_postReviewQuestions?.TrySaveDraft() == false) return;
        SaveDraft();
        try
        {
            var drafts = _draftStore.Load();
            var dialog = new LiteraryImportDraftsWindow(Window.GetWindow(this), drafts, _l, id =>
            {
                _draftStore.Forget(id);
                if (id != _draftId) return;
                _draftSaveTimer.Stop(); _draftTouched = false;
                _draftId = Guid.NewGuid().ToString("N");
            });
            if (dialog.ShowDialog() == true && dialog.SelectedDraft is { } selected) RestoreDraft(selected);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _status.Text = L("RecentLoadFailed") + " " + ex.Message;
            _status.Visibility = Visibility.Visible;
        }
    }

    private async void RestoreDraft(LiteraryImportDraft draft)
    {
        _postReviewQuestions?.Dispose(); _postReviewQuestions = null; _postReviewEntry = null;
        _answersTimer.Stop(); SaveAnswers(); _answerInputs.Clear(); _preparationAnswers = null;
        _session?.Dispose(); _session = null; _input = null; _first = null; _groupingOriginal = null;
        _restoringDraft = true;
        try
        {
            _draftId = draft.Id; _draftTouched = true; _draftFinished = false; _draftStep = draft.Step;
            _workSelectionKey = draft.WorkSelectionKey;
            _selectedWorkUnits.Clear(); _selectedWorkUnits.UnionWith(draft.SelectedWorkUnitIds);
            _selectedPreviewUnits.Clear(); _selectedPreviewUnits.UnionWith(draft.SelectedUnitIds);
            _splitPreviewGroups.Clear(); _splitPreviewGroups.UnionWith(draft.SplitGroupKeys);
            _confirmedPreviewGroups.Clear(); _confirmedPreviewGroups.UnionWith(draft.ConfirmedGroupKeys);
            _editedPreviewTitles.Clear();
            foreach (var (key, value) in draft.EditedGroupNames) _editedPreviewTitles[key] = value;
            _draftFolder.Text = draft.Folder; _draftSourceFile.Text = draft.SourcePath;
            _draftProjectName.Text = draft.ProjectName; _draftWorkTitle.Text = draft.WorkTitle;
        }
        finally { _restoringDraft = false; }
        _showingLegacy = false; _showingDialogSelection = false; _showingWorkChoice = false;
        _parsedSourcePath = _parsedFolderPath = _parsedSessionId = null;
        if (draft.SessionRoot.Length == 0 || !File.Exists(Path.Combine(draft.SessionRoot, "session.json")))
        {
            ShowLanding(); SaveDraft("source"); return;
        }
        var continueAnalysis = false;
        await RunAsync(async ct =>
        {
            _session = await Task.Run(() => ImportSession.Open(draft.SessionRoot), ct);
            _input = await Task.Run(() => DeepSeekImportReader.ReadAsync(_session, ct), ct);
            _parsedSourcePath = draft.SourcePath; _parsedFolderPath = draft.Folder;
            _parsedSessionId = _session.State.Id;
            if (draft.Step == "review" && _session.State.ProjectPath.Length > 0)
            {
                var entry = _store.Load().Projects.SingleOrDefault(p => p.Id == _session.State.ProjectId);
                if (entry is not null) { RestoreBookStep(entry); SaveDraft("review"); return; }
            }
            if ((draft.Step is "analysis" or "works") && draft.DialogIds.Length > 0)
            {
                _session.State.Conversations = draft.DialogIds;
                _analysisStarted = false;
                _analysisTime = TimeSpan.Zero;
                if (draft.Step == "works" || _session.State.Stage == "project-choice")
                {
                    ShowAnalysisScreen(); continueAnalysis = true; return;
                }
                ShowAnalysisScreen(); SaveDraft("analysis"); return;
            }
            ShowDialogSelection();
            foreach (var (box, id) in _conversations)
                box.IsChecked = draft.DialogIds.Contains(id) || _session.State.Conversations.Contains(id);
            SaveDraft("dialogs");
        });
        if (continueAnalysis) StartBackgroundImport(() => CaptureImportOperation("analysis", draft.DialogIds));
    }

    private void FinishDraft()
    {
        _draftFinished = true;
        _draftSaveTimer.Stop();
        try { _draftStore.Forget(_draftId); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _status.Text = L("RecentSaveFailed") + " " + ex.Message; _status.Visibility = Visibility.Visible; }
    }
}
