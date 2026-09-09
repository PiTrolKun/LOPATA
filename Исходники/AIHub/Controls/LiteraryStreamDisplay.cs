using System.Text;
using System.Windows.Threading;
using AIHub.Models;
using TextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls;

/// <summary>One background-priority UI tick, not one dispatcher operation per token.</summary>
public sealed class LiteraryStreamDisplay : IProgress<ModelStreamChunk>, IDisposable
{
    private readonly object _gate = new();
    private readonly StringBuilder _received = new();
    private readonly TextBoxBase _target;
    private readonly DispatcherTimer _timer;
    private int _shown;
    private bool _closed;
    public LiteraryStreamDisplay(TextBoxBase target)
    {
        _target = target;
        target.IsUndoEnabled = false;
        _timer = new DispatcherTimer(DispatcherPriority.Background, target.Dispatcher)
            { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    public void Report(ModelStreamChunk value)
    {
        lock (_gate) { if (!_closed) _received.Append(value.Text); }
    }

    public string Snapshot() { lock (_gate) return _received.ToString(); }

    // Called on the UI thread after the old HTTP stream is disposed and before a retry starts.
    public void Reset(string prefix) => Reset(() =>
    {
        if (_target is TextBox text) text.Text = prefix;
        else if (_target is System.Windows.Controls.RichTextBox rich)
        {
            rich.Document.Blocks.Clear();
            rich.AppendText(prefix);
        }
    });

    public void Reset(Action resetTranscript)
    {
        _target.Dispatcher.VerifyAccess();
        lock (_gate)
        {
            if (_closed) throw new ObjectDisposedException(nameof(LiteraryStreamDisplay));
            _received.Clear(); _shown = 0;
        }
        resetTranscript();
    }

    private void OnTick(object? sender, EventArgs e) => Drain();

    private bool Drain()
    {
        string next;
        lock (_gate)
        {
            var count = Math.Min(4096, _received.Length - _shown);
            if (count == 0) return false;
            next = _received.ToString(_shown, count);
            _shown += count;
        }
        var follow = _target.VerticalOffset + _target.ViewportHeight >= _target.ExtentHeight - 2;
        if (_target is LiteraryTranscript transcript) transcript.AppendResponse(next);
        else _target.AppendText(next);
        if (follow) _target.ScrollToEnd();
        return true;
    }

    public async Task CompleteAsync()
    {
        lock (_gate) _closed = true;
        _timer.Stop();
        while (Drain()) await Dispatcher.Yield(DispatcherPriority.Background);
    }

    public void Dispose()
    {
        lock (_gate) _closed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
    }
}
