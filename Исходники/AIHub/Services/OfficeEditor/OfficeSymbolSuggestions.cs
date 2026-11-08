using System.Text.RegularExpressions;

namespace AIHub.Services.OfficeEditor;

internal sealed record OfficeSymbolToken(int Start, int Length, string Text);

internal sealed class OfficeSymbolSuggestions
{
    private readonly Dictionary<string, OfficeUnicodeSymbol[]> _words;
    private readonly string[] _orderedWords;
    private readonly Dictionary<string, OfficeUnicodeSymbol> _symbols;
    private static readonly Dictionary<string, string[]> Faces = new(StringComparer.OrdinalIgnoreCase)
    {
        [":-)"] = ["🙂", "😀", "😊"], [":)"] = ["🙂", "😀", "😊"], ["=)"] = ["😄", "😁", "😀", "🙂"],
        [":-D"] = ["😃", "😄", "😁", "😂"], [":D"] = ["😃", "😄", "😁", "😂"], ["=D"] = ["😃", "😄", "😁"],
        ["XD"] = ["😆", "😂", "🤣"], [":-("] = ["🙁", "😞", "😢"], [":("] = ["🙁", "😞", "😢"],
        [";)"] = ["😉", "😜"], [";-)"] = ["😉", "😜"], [":P"] = ["😛", "😜", "😝"], [":-P"] = ["😛", "😜", "😝"],
        ["<3"] = ["❤️", "💕", "💖", "💗", "💓"]
    };

    internal OfficeSymbolSuggestions(IEnumerable<OfficeUnicodeSymbol> symbols)
    {
        _symbols = symbols.Where(s => s.Emoji || s.Category == "Sc").ToDictionary(s => s.Text, StringComparer.Ordinal);
        var words = new Dictionary<string, List<OfficeUnicodeSymbol>>(StringComparer.Ordinal);
        foreach (var symbol in _symbols.Values)
        {
            var tokens = Regex.Matches(symbol.Name + " " + symbol.RussianName + " " + symbol.Keywords, @"\p{L}[\p{L}\p{M}]+").Select(m => Normalize(m.Value)).Distinct();
            foreach (var token in tokens)
            {
                if (!words.TryGetValue(token, out var entries)) words[token] = entries = [];
                entries.Add(symbol);
            }
        }
        _words = words.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal);
        _orderedWords = _words.Keys.Order(StringComparer.Ordinal).ToArray();
    }

    internal OfficeUnicodeSymbol[] Find(string word)
    {
        if (Faces.TryGetValue(word, out var faces)) return faces.Where(_symbols.ContainsKey).Select(f => _symbols[f]).ToArray();
        var query = Normalize(word);
        if (query.Length is < 2 or > 40) return [];
        var start = Array.BinarySearch(_orderedWords, query, StringComparer.Ordinal);
        if (start < 0) start = ~start;
        var candidates = new Dictionary<string, (OfficeUnicodeSymbol Symbol, int Score)>(StringComparer.Ordinal);
        for (var i = start; i < _orderedWords.Length && _orderedWords[i].StartsWith(query, StringComparison.Ordinal); i++)
        {
            foreach (var symbol in _words[_orderedWords[i]])
            {
                var primary = PrimaryName(symbol, query);
                var score = (_orderedWords[i] == query ? 100 : 0) + (symbol.Category == "Sc" ? 20 : 0)
                    + (primary == query ? 500 : primary.StartsWith(query, StringComparison.Ordinal) ? 80 : 0);
                if (!candidates.TryGetValue(symbol.Text, out var previous) || score > previous.Score) candidates[symbol.Text] = (symbol, score);
            }
        }
        return candidates.Values.OrderByDescending(c => c.Score).ThenBy(c => PrimaryName(c.Symbol, query).Length).ThenBy(c => c.Symbol.EmojiOrder)
            .Take(8).Select(c => c.Symbol).ToArray();
    }

    internal static OfficeSymbolToken? TokenAt(string text, int caret)
    {
        if (caret < 0 || caret > text.Length) return null;
        var first = Math.Max(0, caret - 64); var prefix = text[first..caret];
        foreach (var face in Faces.Keys.OrderByDescending(s => s.Length))
            if (prefix.EndsWith(face, StringComparison.OrdinalIgnoreCase) && Boundary(text, caret - face.Length))
                return new(caret - face.Length, face.Length, prefix[^face.Length..]);
        var match = Regex.Match(prefix, @"[\p{L}\p{M}]{2,40}$");
        return match.Success && Boundary(text, first + match.Index) ? new(first + match.Index, match.Length, match.Value) : null;
    }

    private static bool Boundary(string text, int start) => start == 0 || !(char.IsLetterOrDigit(text[start - 1]) || "@/:_".Contains(text[start - 1]));
    private static string Normalize(string word) => word.ToLowerInvariant().Replace('ё', 'е');
    private static string PrimaryName(OfficeUnicodeSymbol symbol, string query) => Normalize(
        query.Any(c => c is >= 'а' and <= 'я') && symbol.RussianName.Length > 0 ? symbol.RussianName : symbol.Name);
}
