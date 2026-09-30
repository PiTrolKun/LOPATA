using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.LiteraryImport;

namespace AIHub.Controls;

public sealed record ImportBackgroundInput(string SessionId, string SourceHash, LiteraryImportDraft Draft,
    string Action, bool Legacy, string[] Dialogs, string Work, string[]? Units,
    string Parent, string Name, string Genre, ImportDecision[]? First = null);
public sealed record ImportBackgroundResult(ImportBackgroundInput Input, string? ProjectId, string? AnalysisArtifactId);

public sealed partial class LiteraryImportControl
{
    internal const string BackgroundKind = "literary.import";
    internal ImportInference? BackgroundInference { get; set; }
    internal string? CurrentImportRoot => _session?.Root;

    private void StartBackgroundImport(Func<ImportBackgroundInput> capture)
    {
        if (IsBusy) return;
        try
        {
            var input = capture();
            var pending = ApplicationBackgroundOperations.Current is { HasPending: true, IsRunning: false } host ? host.State : null;
            var previous = pending?.Kind == BackgroundKind ? pending.Input.Deserialize<ImportBackgroundInput>() : null;
            // A deliberate retry from this form resumes its own failed stage. Do not replace
            // an unrelated paused task, and persist the newly reviewed selection before replay.
            var retry = previous is not null && previous.SessionId == input.SessionId && previous.Action == input.Action
                ? pending! with { Input = JsonSerializer.SerializeToElement(input) } : null;
            _ = RunBackgroundImportAsync(input, retry);
        }
        catch (Exception error) { _status.Text = L("Failed") + " " + error.Message; _status.Visibility = System.Windows.Visibility.Visible; }
    }

    private ImportBackgroundInput CaptureImportOperation(string action, string[]? dialogs = null,
        string work = "", string[]? units = null, string parent = "", string name = "", string genre = "")
    {
        if (_session is null || _input is null) throw new InvalidOperationException("Import source is not ready.");
        if (!SaveAnswers() || !SaveDraft()) throw new IOException("Import inputs could not be saved.");
        var draft = new LiteraryImportDraft(_draftId, _draftFolder.Text.Trim(), _draftSourceFile.Text.Trim(),
            _draftProjectName.Text.Trim(), _draftWorkTitle.Text.Trim(), _session.Root, _draftStep,
            dialogs ?? _session.State.Conversations, DateTimeOffset.Now)
        {
            SelectedUnitIds = _selectedPreviewUnits.ToArray(), SplitGroupKeys = _splitPreviewGroups.ToArray(),
            ConfirmedGroupKeys = _confirmedPreviewGroups.ToArray(), EditedGroupNames = new(_editedPreviewTitles),
            WorkSelectionKey = _workSelectionKey, SelectedWorkUnitIds = _selectedWorkUnits.ToArray()
        };
        return new(_session.State.Id, _session.State.SourceHash, draft, action, _showingLegacy,
            dialogs ?? _session.State.Conversations, work, units, parent, name, genre, action == "assembly" ? _first?.ToArray() : null);
    }

    internal async Task ResumeBackgroundImportAsync(BackgroundOperationState state, CancellationToken token)
    {
        var input = state.Input.Deserialize<ImportBackgroundInput>() ?? throw new InvalidDataException("Missing import input.");
        if (state.Project is null || !Path.GetFullPath(state.Project).Equals(Path.GetFullPath(input.Draft.SessionRoot), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Import checkpoint points to another session.");
        await RunBackgroundImportAsync(input, state, token);
    }

    private Task RunBackgroundImportAsync(ImportBackgroundInput input, BackgroundOperationState? restored = null,
        CancellationToken lifetime = default)
        => RunAsync(async token =>
        {
            await ApplicationBackgroundOperations.RunAsync(BackgroundKind, L("Title"), input.Draft.SessionRoot, input, async ct =>
            {
                try
                {
                    await OpenBackgroundImportAsync(input, ct);
                    var id = ApplicationBackgroundOperations.Current?.State?.Id;
                    var receipt = id is null ? null : _session!.ReadLast<ImportBackgroundResult>("background-result/" + id);
                    if (receipt is not null) { RenderBackgroundImportResult(receipt); return true; }
                    if (input.Action == "analysis")
                    {
                        _analysisStarted = true;
                        if (!input.Legacy) ShowAnalysisScreen();
                        _groupingOriginal = await Task.Run(() => Pipeline().AnalyzeAsync(_input!, input.Dialogs, Progress(), ct), ct);
                        ct.ThrowIfCancellationRequested();
                        _first = ImportGrouping.Read(_session!, _groupingOriginal);
                        _analysisStarted = false;
                        if (input.Legacy) ShowWorks(); else ShowAnalyzedWorks();
                        RequireImportDraft("works");
                    }
                    else if (input.Action == "assembly")
                    {
                        _groupingOriginal ??= await Task.Run(() => Pipeline().AnalyzeAsync(_input!, input.Dialogs, Progress(), ct), ct);
                        _first = input.First ?? ImportGrouping.Read(_session!, _groupingOriginal);
                        var assembly = await Task.Run(() => Pipeline().AssembleAsync(_input!, _first!, input.Work, Progress(), ct,
                            input.Units?.ToHashSet(StringComparer.Ordinal)), ct);
                        ct.ThrowIfCancellationRequested();
                        if (!SaveAnswers()) throw new IOException("Import answers could not be saved.");
                        var genre = input.Legacy ? input.Genre : _preparationAnswers!.Values.GetValueOrDefault("6", "").Trim();
                        if (genre.Length == 0) genre = I("Не указано", "Not specified");
                        var entry = await Task.Run(() => ImportProjectBuilder.Build(_session!, _input!, assembly,
                            _store, input.Parent, input.Name, genre, _language), ct);
                        if (input.Legacy)
                        {
                            await Task.Run(() => ImportCompletion.CompleteAsync(_session!, entry, _runtime!, _language, Progress(), ct), ct);
                            ct.ThrowIfCancellationRequested(); FinishDraft(); ShowResult(entry);
                        }
                        else
                        {
                            await Task.Run(() => ImportBookReviewPackage.Export(_session!, entry, _language, ct), ct);
                            ct.ThrowIfCancellationRequested(); _analysisStarted = false; ShowBookReview(entry); RequireImportDraft("review");
                        }
                    }
                    else throw new InvalidDataException("Unknown import operation.");
                    if (id is not null) _session!.AddJson("background-result/" + id,
                        new ImportBackgroundResult(input, input.Action == "analysis" ? null : _session.State.ProjectId,
                            _session.State.Artifacts.LastOrDefault(a => a.Step.StartsWith("pass1/", StringComparison.Ordinal) && a.Status == "complete")?.Id));
                    return true;
                }
                finally
                {
                    // Input fields may remain editable during analysis. A paused operation must
                    // retain those edits before the controller confirms model retirement.
                    if (ct.IsCancellationRequested)
                    {
                        if (!SaveAnswers() || !SaveDraft()) throw new IOException("Import pause checkpoint could not be saved.");
                        _session?.Save();
                    }
                }
            }, token, restored);
        }, keepBodyEnabled: !input.Legacy, lifetime: lifetime);

    private async Task OpenBackgroundImportAsync(ImportBackgroundInput input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (input.Draft is null || string.IsNullOrWhiteSpace(input.Draft.SessionRoot)
            || !Guid.TryParseExact(input.SessionId, "N", out _) || input.Dialogs is null || input.Dialogs.Length == 0
            || input.Dialogs.Distinct(StringComparer.Ordinal).Count() != input.Dialogs.Length
            || input.Action is not ("analysis" or "assembly")) throw new InvalidDataException("Invalid import operation input.");
        if (_session?.Root.Equals(Path.GetFullPath(input.Draft.SessionRoot), StringComparison.OrdinalIgnoreCase) != true)
        {
            if (!SaveAnswers()) throw new IOException("Import answers could not be saved.");
            _answersTimer.Stop(); _answerInputs.Clear(); _preparationAnswers = null;
            _session?.Dispose(); _session = null; _input = null; _first = null; _groupingOriginal = null;
            _session = await Task.Run(() => ImportSession.Open(input.Draft.SessionRoot), token);
            var saved = _draftStore.Load().FirstOrDefault(d => d.Id == input.Draft.Id && d.SessionRoot.Equals(_session.Root, StringComparison.OrdinalIgnoreCase));
            ApplyBackgroundImportDraft(saved ?? input.Draft);
        }
        else if (await Task.Run(() => ImportSession.HashFile(_session.Source), token) != input.SourceHash)
            throw new InvalidDataException("Import source changed.");
        token.ThrowIfCancellationRequested();
        if (_session.State.Id != input.SessionId || _session.State.SourceHash != input.SourceHash)
            throw new InvalidDataException("Import source changed.");
        _input ??= await Task.Run(() => DeepSeekImportReader.ReadAsync(_session, token), token);
        if (input.Dialogs.Any(id => !_input.Conversations.Any(c => c.Id == id))) throw new InvalidDataException("Unknown import dialog.");
        _parsedSessionId = _session.State.Id;
        _parsedSourcePath = _draftSourceFile.Text.Trim(); _parsedFolderPath = _draftFolder.Text.Trim();
        _showingLegacy = input.Legacy;
        _preparationAnswers ??= new ImportPreparationAnswers(_session.Root);
    }

    private void ApplyBackgroundImportDraft(LiteraryImportDraft draft)
    {
        _restoringDraft = true;
        try
        {
            _draftId = draft.Id; _draftTouched = true; _draftFinished = false; _draftStep = draft.Step;
            _workSelectionKey = draft.WorkSelectionKey;
            _selectedWorkUnits.Clear(); _selectedWorkUnits.UnionWith(draft.SelectedWorkUnitIds);
            _selectedPreviewUnits.Clear(); _selectedPreviewUnits.UnionWith(draft.SelectedUnitIds);
            _splitPreviewGroups.Clear(); _splitPreviewGroups.UnionWith(draft.SplitGroupKeys);
            _confirmedPreviewGroups.Clear(); _confirmedPreviewGroups.UnionWith(draft.ConfirmedGroupKeys);
            _editedPreviewTitles.Clear(); foreach (var pair in draft.EditedGroupNames) _editedPreviewTitles[pair.Key] = pair.Value;
            _draftFolder.Text = draft.Folder; _draftSourceFile.Text = draft.SourcePath;
            _draftProjectName.Text = draft.ProjectName; _draftWorkTitle.Text = draft.WorkTitle;
        }
        finally { _restoringDraft = false; }
    }

    private void RequireImportDraft(string step)
    { if (!SaveDraft(step)) throw new IOException("Import result checkpoint could not be saved."); }

    private void RenderBackgroundImportResult(ImportBackgroundResult receipt)
    {
        if (receipt.ProjectId is { } project)
        {
            var entry = _store.Load().Projects.Single(p => p.Id == project);
            if (receipt.Input.Legacy) ShowResult(entry); else ShowBookReview(entry);
        }
        else
        {
            var artifact = _session!.State.Artifacts.Single(a => a.Id == receipt.AnalysisArtifactId && a.Status == "complete");
            _groupingOriginal = JsonSerializer.Deserialize<ImportDecision[]>(File.ReadAllText(_session.ArtifactPath(artifact)), ImportJson.Options)!;
            _first = _groupingOriginal;
            if (receipt.Input.Legacy) ShowWorks(); else ShowAnalyzedWorks();
        }
        _analysisStarted = false;
    }

    internal async Task<bool> ViewBackgroundImportResultAsync(BackgroundOperationNotice notice, CancellationToken token)
    {
        if (IsBusy) return false;
        ImportBackgroundResult? receipt;
        if (_session?.Root.Equals(Path.GetFullPath(notice.Project!), StringComparison.OrdinalIgnoreCase) == true)
            receipt = _session.ReadLast<ImportBackgroundResult>("background-result/" + notice.Id);
        else
        {
            using var session = ImportSession.Open(notice.Project!);
            receipt = session.ReadLast<ImportBackgroundResult>("background-result/" + notice.Id);
        }
        if (receipt is null) return false;
        if (!Path.GetFullPath(notice.Project!).Equals(Path.GetFullPath(receipt.Input.Draft.SessionRoot), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Import result points to another session.");
        await OpenBackgroundImportAsync(receipt.Input, token);
        RenderBackgroundImportResult(receipt);
        return true;
    }
}
