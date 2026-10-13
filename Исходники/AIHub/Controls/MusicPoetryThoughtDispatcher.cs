using System.Text;
using System.Windows.Threading;
using AIHub.Services;

namespace AIHub.Controls;

/// <summary>One pending UI pass, no timer or artificial generation delay.</summary>
internal sealed class MusicPoetryThoughtDispatcher(Dispatcher dispatcher, Action<MusicPoetryStreamUpdate> apply)
{
    private readonly object _gate = new();
    private readonly StringBuilder _text = new();
    private bool _end, _scheduled, _closed;
    internal void Report(MusicPoetryStreamUpdate update)
    {
        lock (_gate)
        {
            if (_closed) return;
            _text.Append(update.Text); _end |= update.ReasoningEnded;
            if (_scheduled) return;
            _scheduled = true;
        }
        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Drain));
    }
    internal void Finish()
    {
        dispatcher.VerifyAccess(); Drain(); lock (_gate) _closed = true;
    }
    private void Drain()
    {
        string text; bool end;
        lock (_gate) { text = _text.ToString(); _text.Clear(); end = _end; _end = false; _scheduled = false; }
        if (text.Length > 0 || end) apply(new(text, end));
    }
}
