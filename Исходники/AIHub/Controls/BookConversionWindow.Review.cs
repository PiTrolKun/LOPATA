using System.Windows.Threading;
using AIHub.Services.Books;

namespace AIHub.Controls;

public sealed partial class BookConversionWindow
{
    private Task<BookReviewDecision> ReviewBeforeSaveAsync(BookReviewSession session, CancellationToken token) =>
        Dispatcher.InvokeAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            BackgroundUserDecision.RequireVisible(this);
            _status.Text = L("Choice.Waiting");
            var choice = new BookConversionChoiceWindow(this, session, _l);
            using (var choiceRegistration = token.Register(() => Dispatcher.BeginInvoke(() =>
            {
                if (choice.IsLoaded) choice.Close();
            })))
            {
                var accepted = choice.ShowDialog() == true;
                token.ThrowIfCancellationRequested();
                if (!accepted) return BookReviewDecision.Cancel;
                if (!choice.ReviewRequested) return BookReviewDecision.Save;
            }
            _status.Text = L("Review.Waiting");
            var review = new BookConversionReviewWindow(this, session, _l);
            using var registration = token.Register(() => Dispatcher.BeginInvoke(() =>
            {
                if (review.IsLoaded) review.Close();
            }));
            review.ShowDialog();
            token.ThrowIfCancellationRequested();
            return review.Decision;
        }, DispatcherPriority.Normal, token).Task;
}
