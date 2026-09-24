using System.IO;
using System.Windows;
using System.Windows.Controls;
using AIHub.Services;
using AIHub.Services.LiteraryImport;

namespace AIHub.Controls;

public sealed partial class LiteraryImportControl
{
    private bool _showingWorkChoice;

    private void ShowAnalyzedWorks()
    {
        if (_first is null || _session is null) return;
        SaveAnswers();
        SetFirstStep(false);
        _showingAnalysis = false;
        _showingBookReview = false;
        _showingDialogSelection = false;
        _showingQuickPreview = false;
        _showingWorkChoice = true;
        _draftStep = "works";
        _body.Children.Clear();

        var layout = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(205) });
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        layout.Children.Add(CreateTopics(3));
        var content = new StackPanel { Margin = new Thickness(18, 0, 8, 0) };
        content.Children.Add(LiteraryUi.Text(I("Шаг 4 · Выбор произведения", "Step 4 · Choose a work"), true));
        content.Children.Add(LiteraryUi.Text(I(
            "Первый проход проверил все выбранные диалоги. Сверьте названия и состав произведений. Файл ещё не создан.",
            "The first pass examined every selected dialog. Review work names and their contents. No file has been created yet.")));
        content.Children.Add(LiteraryUi.Text(I("Предполагаемое название: ", "Expected title: ") + _draftWorkTitle.Text.Trim()));

        _workContinueButton = null;
        content.Children.Add(CreateWorkTree());
        if (_session.State.ProjectPath.Length > 0)
            content.Children.Add(LiteraryUi.Button(I("Вернуться к проверке книги", "Return to book review"), () =>
            {
                var entry = _store.Load().Projects.Single(p => p.Id == _session.State.ProjectId);
                ShowBookReview(entry);
            }, true));
        else
        {
            _workContinueButton = LiteraryUi.Button(I("Продолжить сборку книги", "Continue assembling the book"),
                BuildChosenWork, true);
            content.Children.Add(_workContinueButton);
            RefreshWorkChecks();
        }
        Grid.SetColumn(content, 1);
        layout.Children.Add(content);
        _body.Children.Add(layout);
    }

    private void SaveWorkGrouping(ImportDecision[] edited)
    {
        try
        {
            ImportGrouping.Save(_session!, _groupingOriginal!, edited);
            _first = edited;
            ShowAnalyzedWorks();
            SaveDraft("works");
        }
        catch (Exception ex)
        {
            _status.Text = L("Failed") + " " + ex.Message;
            _status.Visibility = Visibility.Visible;
        }
    }

    private void BuildChosenWork()
    {
        if (IsBusy || _session is null || _input is null || _first is null) return;
        var work = _draftWorkTitle.Text.Trim();
        if (string.IsNullOrWhiteSpace(work) || _selectedWorkUnits.Count == 0)
        {
            _status.Text = L("WorkSelectionRequired");
            _status.Visibility = Visibility.Visible;
            return;
        }
        var parent = _draftFolder.Text.Trim();
        var name = _draftProjectName.Text.Trim();
        if (!LiteraryProjectStore.IsValidProjectName(name) || !Directory.Exists(parent))
        {
            _status.Text = L("MetadataRequired"); _status.Visibility = Visibility.Visible; return;
        }
        var destination = Path.Combine(parent, name);
        if (_session.State.ProjectPath.Length == 0 && Directory.Exists(destination)
            && !string.Equals(_session.State.PlannedPath, Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
        {
            _status.Text = L("DestinationExists"); _status.Visibility = Visibility.Visible; return;
        }
        var selectedUnits = _selectedWorkUnits.ToHashSet(StringComparer.Ordinal);
        _showingWorkChoice = false;
        _analysisStarted = true;
        _lastAnalysisProgress = null;
        _analysisTime = TimeSpan.Zero;
        ShowAnalysisScreen(); SaveDraft("analysis");
        _ = RunAsync(async ct =>
        {
            var progress = Progress();
            var assembly = await Task.Run(() => Pipeline().AssembleAsync(_input!, _first!, work, progress, ct, selectedUnits), ct);
            ct.ThrowIfCancellationRequested();
            progress.Report(new ImportProgress("Export", 0, 0));
            SaveAnswers();
            var genre = _preparationAnswers!.Values.GetValueOrDefault("6", "").Trim();
            if (genre.Length == 0) genre = I("Не указано", "Not specified");
            var entry = await Task.Run(() => ImportProjectBuilder.Build(_session!, _input!, assembly,
                _store, parent, name, genre, _language), ct);
            await Task.Run(() => ImportBookReviewPackage.Export(_session!, entry, _language, ct), ct);
            _analysisStarted = false;
            ShowBookReview(entry);
            SaveDraft("review");
        }, keepBodyEnabled: true);
    }
}
