using System.Windows;
using System.Windows.Controls;
using AIHub.Services;
using AIHub.Services.LiteraryImport;
using ProgressBar = System.Windows.Controls.ProgressBar;

namespace AIHub.Controls;

public sealed partial class LiteraryImportQuestionsControl
{
    private LiteraryChatRuntime? _questionRuntime;
    private CancellationTokenSource? _cancel;

    private async Task SuggestAsync()
    {
        if (IsBusy || _closed || !TrySaveDraft()) return;
        using var cancellation = new CancellationTokenSource(); _cancel = cancellation;
        _body.IsEnabled = false; _status.Text = "";
        var progressText = LiteraryUi.Text(T("ReadingBook"));
        var progressBar = new ProgressBar { IsIndeterminate = true, Height = 8, Margin = new Thickness(0, 8, 0, 8) };
        _activity.Children.Add(progressText); _activity.Children.Add(progressBar);
        _activity.Children.Add(LiteraryUi.Button(T("CancelSuggestion"), () => cancellation.Cancel()));
        try
        {
            var text = await Task.Run(() => new ImportReviewBookStore(_projectRoot).Load().Text, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            var revision = ImportSession.Hash(text);
            _questionRuntime ??= new LiteraryChatRuntime(_projectRoot, preparing: true);
            var progress = new Progress<ImportBookAnswerProgress>(p =>
            {
                if (_closed || cancellation.IsCancellationRequested) return;
                progressText.Text = T(p.Stage == "read" ? "ReadingBook" : "PreparingProposal") +
                    (p.Total > 0 ? $" · {p.Done}/{p.Total}" : "");
                progressBar.IsIndeterminate = p.Total <= 0;
                if (p.Total > 0) progressBar.Value = 100d * p.Done / p.Total;
            });
            var answer = await ImportBookAnswerSuggestion.SuggestAsync(text, _questions.Current,
                _l("Literary.Interview.Q" + _questions.Current), _language,
                (messages, token) => _questionRuntime.InterviewAsync(messages, _ => { }, token,
                    ImportBookAnswerSuggestion.ResponseSchema), progress, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_closed) return;
            if (revision != ImportSession.Hash(new ImportReviewBookStore(_projectRoot).Load().Text))
            { _status.Text = T("BookChanged"); return; }
            _questions.SaveSuggestion(answer, revision); _bookRevision = revision; RenderProposal();
        }
        catch (OperationCanceledException)
        { if (!_closed) _status.Text = T("Cancelled"); }
        catch (Exception)
        {
            // Do not expose backend messages here: they can contain book text.
            if (!_closed) _status.Text = T("SuggestionFailed");
        }
        finally
        {
            _cancel = null;
            if (_closed) { _questionRuntime?.Dispose(); _questionRuntime = null; }
            else { _body.IsEnabled = true; _activity.Children.Clear(); }
        }
    }
}
