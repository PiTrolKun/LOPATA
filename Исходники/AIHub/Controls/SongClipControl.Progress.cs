using System.Windows.Controls;
using System.Windows.Threading;

namespace AIHub.Controls;

public sealed partial class SongClipControl
{
    private const int JournalLimit = 32_000;
    private const int DisplayLineLimit = 500;
    private readonly DispatcherTimer _progressTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(150)
    };
    private readonly Queue<string> _pendingProgress = new();
    private double _pendingFraction;
    private string? _deferredJournal;
    private DispatcherOperation? _journalRefresh;

    private void QueueGenerationProgress(double fraction, string message)
    {
        lock (_pendingProgress)
        {
            _pendingFraction = Math.Max(_pendingFraction, fraction);
            // The worker's full output is on disk. Keep the live view bounded.
            if (_pendingProgress.Count >= 256) _pendingProgress.Dequeue();
            _pendingProgress.Enqueue(message);
        }
    }

    private void FlushGenerationProgress(bool preserveStage = false)
    {
        string[] messages;
        lock (_pendingProgress)
        {
            if (_pendingProgress.Count == 0) return;
            messages = _pendingProgress.ToArray();
            _pendingProgress.Clear();
            _fraction = Math.Max(_fraction, _pendingFraction);
            _pendingFraction = 0;
        }

        var lines = messages.Select(message => ToJournalLine(FormatEngineProgress(message))).ToArray();
        if (!preserveStage) _stage = lines[^1];
        AppendJournalLines(lines);
        if (!preserveStage) UpdateStatus();
    }

    private static string ToJournalLine(string value)
    {
        value = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (value.Length <= DisplayLineLimit) return value;
        var end = char.IsHighSurrogate(value[DisplayLineLimit - 1]) ? DisplayLineLimit - 1 : DisplayLineLimit;
        return value[..end] + "…";
    }

    private void AppendJournalLines(string[] lines)
    {
        var addition = string.Join(Environment.NewLine, lines) + Environment.NewLine;
        var combined = (_deferredJournal ?? _log.Text) + addition;
        var scroll = JournalScrollViewer();
        var follow = scroll is not null && IsFollowingJournal(scroll);
        if (_deferredJournal is null && combined.Length <= JournalLimit)
        {
            _log.AppendText(addition);
            // ScrollViewer queues the request without TextBoxBase.ScrollToEnd's
            // synchronous re-entry into native text measurement.
            if (follow) scroll!.ScrollToBottom();
            return;
        }

        var lineEnd = combined.IndexOf('\n', Math.Max(0, combined.Length - JournalLimit));
        _deferredJournal = combined[(lineEnd + 1)..];
        if (scroll is null || follow) PublishDeferredJournal(scroll);
    }

    private ScrollViewer? JournalScrollViewer() => _log.Template?.FindName("PART_ContentHost", _log) as ScrollViewer;

    private bool IsFollowingJournal(ScrollViewer scroll) => _log.SelectionLength == 0
        && scroll.VerticalOffset >= scroll.ScrollableHeight - 1;

    private void QueueDeferredJournalRefresh()
    {
        if (_deferredJournal is null || _journalRefresh?.Status == DispatcherOperationStatus.Pending) return;
        _journalRefresh = Dispatcher.InvokeAsync(() =>
        {
            var scroll = JournalScrollViewer();
            if (_deferredJournal is not null && scroll is not null && IsFollowingJournal(scroll))
                PublishDeferredJournal(scroll);
        }, DispatcherPriority.Background);
    }

    private void PublishDeferredJournal(ScrollViewer? scroll)
    {
        var text = _deferredJournal!;
        _deferredJournal = null;
        _log.Text = text;
        scroll?.ScrollToBottom();
    }
}
