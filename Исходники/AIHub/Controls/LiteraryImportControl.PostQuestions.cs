using AIHub.Models;
using AIHub.Services.LiteraryImport;

namespace AIHub.Controls;

public sealed partial class LiteraryImportControl
{
    private LiteraryImportQuestionsControl? _postReviewQuestions;
    private LiteraryProjectEntry? _postReviewEntry;

    private void ShowPostReviewQuestions(LiteraryProjectEntry entry)
    {
        if (_session?.State.Stage is not ("book-confirmed" or "workspace-ready")) { ShowBookReview(entry); return; }
        _answersTimer.Stop(); SaveAnswers(); _answerInputs.Clear();
        try
        {
            _preparationAnswers ??= new ImportPreparationAnswers(_session.Root);
            var questions = new LiteraryImportQuestionsControl(entry.ProjectPath, _preparationAnswers, _language, _l, _session);
            questions.OpenWorkspaceRequested += () =>
            {
                FinishDraft(); questions.Dispose(); _postReviewQuestions = null;
                _session?.Dispose(); _session = null; OpenRequested?.Invoke(entry);
            };
            _runtime?.Dispose(); _runtime = null;
            _postReviewQuestions?.Dispose(); _postReviewQuestions = questions; _postReviewEntry = entry;
            SetFirstStep(false);
            _showingAnalysis = _showingWorkChoice = _showingDialogSelection = _showingQuickPreview = false;
            _showingBookReview = true; _body.Children.Clear(); _body.Children.Add(questions);
            _status.Visibility = _progress.Visibility = _sessionLabel.Visibility = System.Windows.Visibility.Collapsed;
            SaveDraft("review");
        }
        catch (Exception ex)
        {
            _status.Text = _l("Literary.Import.PostQuestions.OpenFailed") + " " +
                (ex.Message.StartsWith("Literary.", StringComparison.Ordinal) ? _l(ex.Message) : ex.Message);
            _status.Visibility = System.Windows.Visibility.Visible;
            // The outer import runner can hide its status on success; opening failures must stay visible.
            System.Windows.MessageBox.Show(System.Windows.Window.GetWindow(this), _status.Text,
                _l("Literary.Import.PostQuestions.Title"), System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private void RestoreBookStep(LiteraryProjectEntry entry)
    {
        _preparationAnswers ??= new ImportPreparationAnswers(_session!.Root);
        if (_session!.State.Stage == "workspace-ready" || _session.State.Stage == "book-confirmed" && ImportPostReviewQuestions.HasStarted(_preparationAnswers))
            ShowPostReviewQuestions(entry);
        else ShowBookReview(entry);
    }
}
