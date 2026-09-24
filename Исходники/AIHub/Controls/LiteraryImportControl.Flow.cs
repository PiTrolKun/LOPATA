using System.Diagnostics;
using System.IO;
using System.Windows;
using AIHub.Services;
using AIHub.Services.LiteraryImport;

namespace AIHub.Controls;

public sealed partial class LiteraryImportControl
{
    private ImportDecision[]? _groupingOriginal;
    private async Task RunAsync(Func<CancellationToken, Task> action, bool keepBodyEnabled = false)
    {
        if (IsBusy) return;
        using var cts = new CancellationTokenSource(); _operation = cts;
        var cardOwnsProgress = keepBodyEnabled && _showingAnalysis && !_showingLegacy;
        _status.Visibility = _progress.Visibility = _sessionLabel.Visibility = cardOwnsProgress ? Visibility.Collapsed : Visibility.Visible;
        _body.IsEnabled = keepBodyEnabled; _progress.IsIndeterminate = true;
        var clock = Stopwatch.StartNew();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            _sessionLabel.Text = (_session?.Root ?? "") + $"   {clock.Elapsed:hh\\:mm\\:ss}";
            UpdateAnalysisClock(clock.Elapsed);
        }; timer.Start();
        _status.Text = L("Working");
        var failed = false;
        try { await action(cts.Token); if (_session?.State.Stage is not ("complete" or "partial-result")) _status.Text = L("StepReady"); }
        catch (OperationCanceledException)
        {
            failed = true;
            if (!_showingAnalysis && _session?.State.ProjectPath.Length > 0 && _first is not null) ShowWorks();
            _status.Text = L("Cancelled");
        }
        catch (Exception ex)
        {
            failed = true;
            if (!_showingAnalysis && _session?.State.ProjectPath.Length > 0 && _first is not null) ShowWorks();
            _status.Text = L("Failed") + " " + (ex.Message.StartsWith("Literary.Import.", StringComparison.Ordinal) ? _l(ex.Message) : ex.Message);
            if (cardOwnsProgress) _analysisIssue = _status.Text;
            if (_session is not null) try { _session.State.LastError = ex.GetType().Name + ": " + ex.Message; _session.Save(); } catch (IOException) { }
        }
        finally
        {
            timer.Stop(); _operation = null; _body.IsEnabled = true; _progress.IsIndeterminate = false;
            var navigate = _pendingNavigation;
            _pendingNavigation = null;
            navigate?.Invoke();
            if (failed && navigate is null && _showingAnalysis)
            { _analysisStarted = false; ShowAnalysisScreen(); }
            if (cardOwnsProgress || (!failed && !_showingLegacy && (_showingWorkChoice || _showingBookReview)))
                _status.Visibility = _progress.Visibility = _sessionLabel.Visibility = Visibility.Collapsed;
            if (_showingDialogSelection && !_showingLegacy && !failed)
                _status.Visibility = _progress.Visibility = _sessionLabel.Visibility = Visibility.Collapsed;
            else if (_showingDialogSelection && !_showingLegacy)
                _progress.Visibility = _sessionLabel.Visibility = Visibility.Collapsed;
        }
    }
    private IProgress<ImportProgress> Progress() => new Progress<ImportProgress>(p =>
    {
        _status.Text = L(p.Stage) + (p.Total > 0 ? $" · {p.Done}/{p.Total}" : "");
        _progress.IsIndeterminate = p.Total <= 0;
        if (p.Total > 0) _progress.Value = 100.0 * p.Done / p.Total;
        UpdateAnalysisProgress(p);
    });
    private ImportPipeline Pipeline()
    {
        _runtime ??= new LiteraryChatRuntime();
        return new ImportPipeline(_session!, (messages, step, tokens, ct) => _runtime.ImportAnalyzeAsync(messages, _session!, step, tokens, ct));
    }
    private void Analyze()
    {
        var ids = _conversations.Where(c => c.Box.IsChecked == true).Select(c => c.Id).ToArray();
        _ = RunAsync(async ct =>
        {
            var pipeline = Pipeline(); var progress = Progress();
            _groupingOriginal = await Task.Run(() => pipeline.AnalyzeAsync(_input!, ids, progress, ct), ct);
            _first = ImportGrouping.Read(_session!, _groupingOriginal);
            ShowWorks();
            SaveDraft("works");
        });
    }
    private void ShowWorks()
    {
        var suggestedProjectName = _showingDialogSelection ? _draftProjectName.Text.Trim() : "";
        _showingDialogSelection = false;
        _draftStep = "works";
        _body.Children.Clear(); _works.Items.Clear();
        _body.Children.Add(LiteraryUi.Text(L("ChooseWork"), true));
        foreach (var name in _first!.Select(d => d.Project).Where(p => p.Length > 0).Distinct()) _works.Items.Add(name);
        if (_works.Items.Count == 0) throw new InvalidDataException("Literary.Import.NoBook");
        _works.SelectedItem = _session!.State.SelectedProject;
        if (_works.SelectedIndex < 0) _works.SelectedIndex = 0;
        _works.IsEnabled = _session.State.PlannedPath.Length == 0;
        _body.Children.Add(_works);
        if (_session.State.PlannedPath.Length > 0)
            _body.Children.Add(LiteraryUi.Button(L("Reassemble"), () => _ = RunAsync(async ct =>
            {
                var fork=await Task.Run(()=>_session.ForkForAssembly(ct),ct);
                _session.Dispose(); _session=fork; _name.Text=""; ShowWorks();
            })));
        if (_session.State.PlannedPath.Length == 0 && _groupingOriginal is not null)
        {
            _body.Children.Add(LiteraryUi.Button(L("MergeWorks"), () =>
            {
                var dialog = new LiteraryImportWorkNamesWindow(Window.GetWindow(this), _input!, _first!, _l);
                if (dialog.ShowDialog() != true) return;
                try { ImportGrouping.Save(_session, _groupingOriginal, dialog.Decisions); _first=dialog.Decisions; ShowWorks(); }
                catch(Exception ex) { _status.Text=L("Failed")+" "+ex.Message; }
            }));
            if (_first!.Select(d=>d.Project).Where(p=>p.Length>0).Distinct().Any(a=>_first!.Any(d=>d.Project!=a && ImportWorkNames.Similar(a,d.Project))))
                _body.Children.Add(LiteraryUi.Text(L("SimilarNotice")));
        }
        if (_session.State.PlannedPath.Length == 0 && _groupingOriginal is not null)
            _body.Children.Add(LiteraryUi.Button(L("EditGroups"), () =>
            {
                var dialog = new LiteraryImportGroupingWindow(Window.GetWindow(this), _input!, _first!, _l);
                if (dialog.ShowDialog() != true) return;
                try
                {
                    ImportGrouping.Save(_session, _groupingOriginal, dialog.Decisions);
                    _first = dialog.Decisions; ShowWorks();
                }
                catch (Exception ex) { _status.Text = L("Failed") + " " + ex.Message; }
            }));
        _body.Children.Add(LiteraryUi.Text(L("ProjectName"))); _name.Text = suggestedProjectName;
        if (_session.State.PlannedPath.Length > 0)
        { _name.Text = Path.GetFileName(_session.State.PlannedPath); _genre.Text = _session.State.Genre; _folder.Text = Path.GetDirectoryName(_session.State.PlannedPath)!; }
        if (_session.State.ProjectPath.Length > 0)
        {
            var project = LiteraryProjectStore.ReadProject(_session.State.ProjectPath);
            _name.Text = project.ProjectName; _genre.Text = project.CustomGenres;
            _folder.Text = Path.GetDirectoryName(_session.State.ProjectPath)!;
        }
        _body.Children.Add(_name);
        _body.Children.Add(LiteraryUi.Text(L("Genre"))); _body.Children.Add(_genre);
        _body.Children.Add(LiteraryUi.Text(L("Folder"))); _body.Children.Add(_folder);
        _body.Children.Add(LiteraryUi.Button(L("ChooseFolder"), ChooseFolder));
        _body.Children.Add(LiteraryUi.Text(L("ResultHint")));
        _body.Children.Add(LiteraryUi.Button(L("Create"), Create, true));
        if (_session.State.ProjectPath.Length > 0)
            _body.Children.Add(LiteraryUi.Button(L("ExportSaved"), () => _ = RunAsync(async ct =>
            {
                var entry = _store.Load().Projects.Single(p => p.Id == _session.State.ProjectId);
                await Task.Run(() => ImportCompletion.Export(_session, entry, _language, ct), ct);
                _status.Text = L("Exported") + " " + _session.State.RagStatus + " / " + _session.State.MemoryStatus;
            })));
    }
    private void Create()
    {
        var name = _name.Text.Trim(); var folder = _folder.Text; var genre = _genre.Text.Trim();
        var work = _works.SelectedItem as string;
        if (!LiteraryProjectStore.IsValidProjectName(name) || string.IsNullOrWhiteSpace(genre) || work is null)
        { _status.Text = L("MetadataRequired"); return; }
        if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder))
        { _status.Text = L("ChooseFolder"); return; }
        if (Directory.Exists(Path.Combine(folder, name)) && _session!.State.PlannedPath.Length == 0)
        { _status.Text = L("DestinationExists"); return; }
        _ = RunAsync(async ct =>
        {
            var progress = Progress(); var pipeline = Pipeline();
            var assembly = await Task.Run(() => pipeline.AssembleAsync(_input!, _first!, work, progress, ct), ct);
            ct.ThrowIfCancellationRequested();
            var entry = await Task.Run(() => ImportProjectBuilder.Build(_session!, _input!, assembly, _store, folder, name, genre, _language), ct);
            await Task.Run(() => ImportCompletion.CompleteAsync(_session!, entry, _runtime!, _language, progress, ct), ct);
            FinishDraft();
            ShowResult(entry);
        });
    }
}
