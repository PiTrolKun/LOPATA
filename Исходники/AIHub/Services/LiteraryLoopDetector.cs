using System.Text;

namespace AIHub.Services;

public sealed record LiteraryLoopEvidence(string Kind, int PeriodWords, int RepeatedWords);
public sealed class LiteraryLoopException(LiteraryLoopEvidence evidence) : Exception("Repeated generation detected.")
{
    public LiteraryLoopEvidence Evidence { get; } = evidence;
}

/// <summary>Incremental version of the measured stand detector. O(128) per word; no transcript rescans.</summary>
public sealed class LiteraryLoopDetector
{
    private const int MaxPeriod = 128, RingSize = MaxPeriod + 1;
    private readonly string?[] _words = new string?[RingSize], _normalized = new string?[RingSize];
    private readonly int[] _literalRuns = new int[RingSize], _numericRuns = new int[RingSize];
    private readonly StringBuilder _word = new();
    private long _count;
    private bool _oversizedWord;

    public void Append(string chunk)
    {
        foreach (var c in chunk)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                // An unbroken huge token cannot grow detector memory without bound.
                if (_word.Length < 512) _word.Append(char.ToLowerInvariant(c));
                else _oversizedWord = true;
            }
            else CompleteWord();
        }
    }

    public void Complete() => CompleteWord();

    private void CompleteWord()
    {
        if (_word.Length == 0) return;
        var word = _word.ToString(); _word.Clear();
        if (_oversizedWord)
        {
            _oversizedWord = false;
            Array.Clear(_literalRuns); Array.Clear(_numericRuns);
            Array.Clear(_words); Array.Clear(_normalized); _count = 0;
            return;
        }
        var normalized = word.All(char.IsDigit) ? "#" : word;
        for (var width = 1; width <= Math.Min(MaxPeriod, _count); width++)
        {
            var previous = (int)((_count - width) % RingSize);
            var literal = _literalRuns[width] = word == _words[previous] ? _literalRuns[width] + 1 : 0;
            var numeric = _numericRuns[width] = normalized == _normalized[previous] ? _numericRuns[width] + 1 : 0;
            var span = literal + width;
            if (literal >= width * 2 && ((span >= 48 && span / width >= 6) || (span >= 192 && span / width >= 3)))
                throw new LiteraryLoopException(new("literal", width, span));
            span = numeric + width;
            if (numeric >= width * 2 && span >= 128 && span / width >= 6)
                throw new LiteraryLoopException(new("numeric_template", width, span));
        }
        var index = (int)(_count++ % RingSize);
        _words[index] = word; _normalized[index] = normalized;
    }
}
