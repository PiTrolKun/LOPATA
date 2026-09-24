using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AIHub.Services;
using AIHub.Services.LiteraryImport;
using ListBox = System.Windows.Controls.ListBox;
using ProgressBar = System.Windows.Controls.ProgressBar;
using TextBox = System.Windows.Controls.TextBox;
using MessageBox = System.Windows.MessageBox;

namespace AIHub.Controls;

public sealed partial class LiteraryImportControl
{
    private bool _showingAnalysis, _showingBookReview, _analysisStarted;
    private ImportPreparationAnswers? _preparationAnswers;
    private ProgressBar _analysisBar = new() { Height = 12, IsIndeterminate = true };
    private TextBlock _analysisStage = new() { TextWrapping = TextWrapping.Wrap };
    private TextBlock _analysisElapsed = new(), _analysisRemaining = new();
    private readonly DispatcherTimer _answersTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly Dictionary<string, TextBox> _answerInputs = new();
    private ListBox _referenceFiles = new() { Height = 80 };
    private TimeSpan _analysisTime;
    private ImportProgress? _lastAnalysisProgress;
    private string _analysisIssue = "";
    private TextBlock _analysisIssueLabel = new() { TextWrapping = TextWrapping.Wrap };

    private string I(string ru, string en) => _language == "ru" ? ru : en;

    private void StartFullAnalysis()
    {
        if (IsBusy) return;
        var ids = _showingDialogSelection
            ? _conversations.Where(c => c.Box.IsChecked == true).Select(c => c.Id).ToArray()
            : _session?.State.Conversations ?? [];
        if (_session is null || _input is null || ids.Length == 0)
        { _status.Text = I("Выберите хотя бы один диалог.", "Select at least one dialog."); _status.Visibility = Visibility.Visible; return; }
        if (_session.State.ProjectPath.Length > 0)
        {
            var entry = _store.Load().Projects.Single(p => p.Id == _session.State.ProjectId);
            ShowBookReview(entry); return;
        }
        if (_session.State.PlannedPath.Length > 0 &&
            !_session.State.Conversations.Order().SequenceEqual(ids.Order()))
        { _status.Text = L("LockedSelection"); _status.Visibility = Visibility.Visible; return; }
        var destination = Path.Combine(_draftFolder.Text.Trim(), _draftProjectName.Text.Trim());
        if (_session.State.ProjectPath.Length == 0 && Directory.Exists(destination)
            && !string.Equals(_session.State.PlannedPath, Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
        { _status.Text = L("DestinationExists"); _status.Visibility = Visibility.Visible; return; }
        try { _preparationAnswers = new ImportPreparationAnswers(_session.Root); }
        catch (Exception ex)
        { _status.Text = I("Не удалось открыть ответы: ", "Could not open answers: ") + ex.Message;
            _status.Visibility = Visibility.Visible; return; }
        _session.State.Conversations = ids;
        _session.Save();
        _first = null;
        _groupingOriginal = null;
        _lastAnalysisProgress = null;
        _analysisTime = TimeSpan.Zero;
        _analysisIssue = "";
        _showingQuickPreview = false; _showingAnalysis = true;
        _analysisStarted = true;
        ShowAnalysisScreen(); SaveDraft("analysis");
        _ = RunAsync(async ct =>
        {
            var progress = Progress(); var pipeline = Pipeline();
            _groupingOriginal = await Task.Run(() => pipeline.AnalyzeAsync(_input!, ids, progress, ct), ct);
            _first = ImportGrouping.Read(_session!, _groupingOriginal);
            _analysisStarted = false;
            ShowAnalyzedWorks();
            SaveDraft("works");
        }, keepBodyEnabled: true);
    }

    private void ShowAnalysisScreen()
    {
        if (_session is null) return;
        SaveAnswers();
        _analysisBar = new ProgressBar { Height = 12, IsIndeterminate = true };
        _analysisStage = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _analysisElapsed = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _analysisRemaining = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _referenceFiles = new ListBox { MaxHeight = 160 };
        SetFirstStep(false); _showingQuickPreview = false; _showingDialogSelection = false; _showingWorkChoice = false;
        _showingAnalysis = true; _showingBookReview = false;
        _preparationAnswers ??= new ImportPreparationAnswers(_session.Root);
        _body.Children.Clear(); _answerInputs.Clear(); _referenceFiles.Items.Clear();
        _body.Children.Add(LiteraryUi.Text(I("Анализ книги и подготовка проекта", "Book analysis and project preparation"), true));
        var grid = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        grid.Children.Add(CreateTopics(_analysisStarted && _first is not null ? 4 : 2));
        var questions = new StackPanel { Margin = new Thickness(14, 0, 18, 0) };
        if (_analysisStarted && _first is not null)
            questions.Children.Add(CreateAnalysisTips());
        else
        {
            questions.Children.Add(LiteraryUi.Text(I("Пока идёт анализ всех выбранных диалогов, можно заполнить сведения для дальнейшей работы. Произведение вы выберете после анализа. Ответы сохраняются; ИИ-допрос здесь не запускается.",
                "While all selected dialogs are analyzed, you can enter details for later work. You will choose the work after analysis. Answers are saved; no AI interview starts.")));
            questions.Children.Add(LiteraryUi.Text(I("Уже указано: ", "Already provided: ") +
                _draftProjectName.Text + " · " + _draftWorkTitle.Text));
            _questionHost = new StackPanel { Margin = new Thickness(0, 20, 0, 0) };
            questions.Children.Add(_questionHost);
            _answersTimer.Tick -= AnswersTimerTick;
            _answersTimer.Tick += AnswersTimerTick;
            RestoreQuestionPosition();
            RenderCurrentQuestion();
        }
        Grid.SetColumn(questions, 1); grid.Children.Add(questions);
        var card = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
            Padding = new Thickness(14), VerticalAlignment = VerticalAlignment.Top };
        card.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        card.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextPrimaryBrush");
        var cardItems = new StackPanel(); card.Child = cardItems;
        cardItems.Children.Add(LiteraryUi.Text(I("Анализ книги", "Book analysis"), true));
        cardItems.Children.Add(_analysisStage);
        _analysisBar.Margin = new Thickness(0, 12, 0, 12);
        cardItems.Children.Add(_analysisBar);
        cardItems.Children.Add(_analysisElapsed); cardItems.Children.Add(_analysisRemaining);
        _analysisIssueLabel = LiteraryUi.Text(_analysisIssue);
        _analysisIssueLabel.TextWrapping = TextWrapping.Wrap;
        _analysisIssueLabel.Visibility = _analysisIssue.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        cardItems.Children.Add(_analysisIssueLabel);
        var pulse = new TextBlock { Text = "●  ●  ●", FontSize = 23, Margin = new Thickness(0, 10, 0, 0) };
        pulse.SetResourceReference(ForegroundProperty, "AccentBrush"); cardItems.Children.Add(pulse);
        if (SystemParameters.ClientAreaAnimation && _analysisStarted)
            pulse.BeginAnimation(OpacityProperty, new DoubleAnimation(.4, 1, TimeSpan.FromSeconds(1.2))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        if (!_analysisStarted && _session.State.ProjectPath.Length == 0)
            cardItems.Children.Add(_first is not null
                ? LiteraryUi.Button(I("Выбрать произведение", "Choose a work"), ShowAnalyzedWorks, true)
                : LiteraryUi.Button(I("Продолжить анализ", "Resume analysis"), StartFullAnalysis, true));
        else if (!_analysisStarted && _session.State.ProjectPath.Length > 0)
            cardItems.Children.Add(LiteraryUi.Button(_session.State.Stage is "book-review" or "book-confirmed"
                ? I("Вернуться к проверке", "Return to review") : I("Завершить сборку книги", "Finish book export"), () =>
            {
                var entry = _store.Load().Projects.Single(p => p.Id == _session.State.ProjectId);
                SaveAnswers();
                if (_session.State.Stage is "book-review" or "book-confirmed") { ShowBookReview(entry); return; }
                _ = RunAsync(async ct =>
                {
                    await Task.Run(() => ImportBookReviewPackage.Export(_session, entry, _language, ct), ct);
                    ShowBookReview(entry); SaveDraft("review");
                });
            }, true));
        _analysisStage.Text = _analysisStarted ? I("Подготовка анализа…", "Preparing analysis…")
            : _first is not null ? I("Первый проход завершён. Выберите произведение.", "First pass complete. Choose a work.")
            : I("Анализ остановлен. Ответы сохранены.", "Analysis stopped. Answers saved.");
        _analysisBar.IsIndeterminate = _analysisStarted;
        _analysisElapsed.Text = I("Прошло: ", "Elapsed: ") + _analysisTime.ToString(@"hh\:mm\:ss");
        _analysisRemaining.Text = I("Оценка времени готовится", "Estimating time");
        var sidebar = new StackPanel();
        sidebar.Children.Add(card);
        var notice = LiteraryUi.Text(L("AnalysisVariationNotice"));
        notice.Margin = new Thickness(2, 10, 2, 0);
        sidebar.Children.Add(notice);
        Grid.SetColumn(sidebar, 2); grid.Children.Add(sidebar);
        _body.Children.Add(grid);
    }

    private void AnswersTimerTick(object? sender, EventArgs e) { _answersTimer.Stop(); SaveAnswers(); }
    private void SaveAnswers()
    {
        if (_preparationAnswers is null) return;
        try
        {
            foreach (var (key, box) in _answerInputs) _preparationAnswers.Set(key, box.Text);
            if (_answerInputs.ContainsKey("6.custom")) SaveGenreAnswer();
            if (_answerInputs.ContainsKey("14.custom")) SaveCultureAnswer();
        }
        catch (Exception ex)
        {
            var message = I("Не удалось сохранить ответы: ", "Could not save answers: ") + ex.Message;
            if (_showingAnalysis && !_showingLegacy)
            {
                _analysisIssue = _analysisIssueLabel.Text = message;
                _analysisIssueLabel.Visibility = Visibility.Visible;
            }
            else { _status.Text = message; _status.Visibility = Visibility.Visible; }
        }
    }

    private void ChooseReferenceFiles()
    {
        SaveAnswers();
        if (!HasOriginalSource())
        { _status.Text = I("Сначала укажите, что первоисточник есть.", "First indicate that an original source exists."); _status.Visibility = Visibility.Visible; return; }
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, CheckFileExists = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        foreach (var file in dialog.FileNames) if (!_referenceFiles.Items.Contains(file)) _referenceFiles.Items.Add(file);
        _preparationAnswers!.Set("10", string.Join('|', _referenceFiles.Items.Cast<string>()));
    }

    private void UpdateAnalysisProgress(ImportProgress p)
    {
        if (!_showingAnalysis) return;
        _lastAnalysisProgress = p;
        _analysisStage.Text = p.Stage switch
        {
            "Pass1" => I("Отмечаем литературный текст и лишние фрагменты", "Marking story text and unrelated passages"),
            "Pass2" => I("Собираем главы и проверяем сомнительное", "Assembling chapters and checking uncertain passages"),
            "Export" => I("Создаём книгу DOCX и историю анализа", "Creating DOCX and analysis history"),
            _ => L(p.Stage)
        } + (p.Total > 0 ? $" · {p.Done}/{p.Total}" : "");
        _analysisBar.IsIndeterminate = p.Total <= 0;
        if (p.Total > 0) _analysisBar.Value = 100.0 * p.Done / p.Total;
        UpdateAnalysisClock(_analysisTime);
    }

    private void UpdateAnalysisClock(TimeSpan elapsed)
    {
        _analysisTime = elapsed;
        if (!_showingAnalysis) return;
        _analysisElapsed.Text = I("Прошло: ", "Elapsed: ") + elapsed.ToString(@"hh\:mm\:ss");
        var p = _lastAnalysisProgress;
        _analysisRemaining.Text = p is { Done: > 1, Total: > 0 } && elapsed.TotalSeconds >= 20
            ? L("RemainingTimeLabel") + "\n" +
              TimeSpan.FromSeconds(Math.Min(86400, elapsed.TotalSeconds / p.Done * (p.Total - p.Done))).ToString(@"hh\:mm\:ss")
            : I("Оценка времени готовится", "Estimating time");
    }

    private void ShowBookReview(AIHub.Models.LiteraryProjectEntry entry)
    {
        _showingAnalysis = false; _showingWorkChoice = false; _showingBookReview = true; _analysisStarted = false;
        _body.Children.Clear();
        _body.Children.Add(LiteraryUi.Text(I("Проверка результата анализа", "Review analysis result"), true));
        _body.Children.Add(LiteraryUi.Text(I("Книга собрана в DOCX. Откройте её здесь, исправьте текст и проверьте пометки. Правки сохраняются.",
            "The DOCX book is ready. Open it here to edit the text and review markings. Edits are saved.")));
        _body.Children.Add(LiteraryUi.Button(I("Открыть редактор книги", "Open book editor"), () =>
        {
            LiteraryImportBookEditorWindow window;
            try
            {
                window = new LiteraryImportBookEditorWindow(Window.GetWindow(this), entry.ProjectPath, _language, _l);
                window.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), ex.Message.StartsWith("Literary.") ? _l(ex.Message) : ex.Message,
                    _l("Literary.BookReview.Title"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (window.Changed) _ = RunAsync(async ct =>
            {
                _session!.State.Stage = "book-review"; _session.Save();
                await Task.Run(() => ImportBookReviewPackage.Export(_session!, entry, _language, ct), ct);
                ShowBookReview(entry);
            });
        }, true));
        _body.Children.Add(LiteraryUi.Button(I("Обновить DOCX и XLSX", "Refresh DOCX and XLSX"), () =>
            _ = RunAsync(async ct => await Task.Run(() => ImportBookReviewPackage.Export(_session!, entry, _language, ct), ct))));
        _body.Children.Add(LiteraryUi.Text(I("DOCX и XLSX лежат в папке Exports/Import проекта.",
            "DOCX and XLSX are in the project's Exports/Import folder.")));
        if (_session?.State.Stage == "book-confirmed")
        {
            _body.Children.Add(LiteraryUi.Button(_l("Literary.Import.PostQuestions.Continue"),
                () => ShowPostReviewQuestions(entry), true));
            return;
        }
        _body.Children.Add(LiteraryUi.Button(I("Подтвердить готовность файла", "Confirm file is ready"), () => ConfirmBook(entry), true));
    }

    private void ConfirmBook(AIHub.Models.LiteraryProjectEntry entry)
    {
        if (MessageBox.Show(Window.GetWindow(this), I("Вы открывали и проверили собранную книгу?", "Have you reviewed the assembled book?"),
            I("Подтверждение 1 из 2", "Confirmation 1 of 2"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (MessageBox.Show(Window.GetWindow(this), I("Качество ваших исправлений влияет на качество всей дальнейшей работы. Подтвердить готовность этого файла?",
            "The quality of your corrections affects all later work. Confirm this file is ready?"),
            I("Подтверждение 2 из 2", "Confirmation 2 of 2"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _ = RunAsync(async ct =>
        {
            SaveAnswers();
            await Task.Run(() => ImportBookReviewPackage.Export(_session!, entry, _language, ct), ct);
            _session!.AddJson("book-confirmed", new { at = DateTimeOffset.UtcNow,
                warning = "The quality of corrections affects later work." });
            _session.State.Stage = "book-confirmed"; _session.Save();
            ShowPostReviewQuestions(entry); SaveDraft("review");
        });
    }
}
