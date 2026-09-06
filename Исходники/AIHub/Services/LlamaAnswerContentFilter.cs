using System.IO;
using System.Text;

namespace AIHub.Services;

// The server normally separates reasoning_content. This also handles an inline
// leading think block without leaking partial tags or reasoning into the answer.
public sealed class LlamaAnswerContentFilter
{
    private const string Opening = "<think>";
    private const string Closing = "</think>";
    private readonly StringBuilder _pending = new();
    private bool _answerStarted;
    private bool _inThought;

    public string Append(string chunk)
    {
        if (_answerStarted) return chunk;
        _pending.Append(chunk);
        var pending = _pending.ToString();
        if (!_inThought)
        {
            var trimmed = pending.TrimStart();
            if (trimmed.Length < Opening.Length && Opening.StartsWith(trimmed, StringComparison.Ordinal))
                return string.Empty;
            if (!trimmed.StartsWith(Opening, StringComparison.Ordinal))
            {
                _answerStarted = true;
                _pending.Clear();
                return pending;
            }
            _inThought = true;
            pending = trimmed[Opening.Length..];
        }
        var end = pending.IndexOf(Closing, StringComparison.Ordinal);
        _pending.Clear();
        if (end < 0)
        {
            // Retain only enough text to recognize a closing tag split across SSE chunks.
            _pending.Append(pending[^Math.Min(pending.Length, Closing.Length - 1)..]);
            return string.Empty;
        }
        _inThought = false;
        _answerStarted = true;
        return pending[(end + Closing.Length)..];
    }

    public string Complete()
    {
        if (_inThought || (!_answerStarted && _pending.ToString().TrimStart().StartsWith('<')))
            throw new InvalidDataException("The model did not finish its reasoning block.");
        var tail = _pending.ToString();
        _pending.Clear();
        return tail;
    }
}
