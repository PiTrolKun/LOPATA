using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIHub.Services.OfficeEditor;

internal enum OfficeSymbolScope { All, Emoji, Arrows, Signs, Math, Currency, Borders, Letters, Marks }
internal sealed record OfficeUnicodeBlock(int First, int Last, string Name);
internal sealed record OfficeUnicodeSymbol(int First, string Text, string Name, string Category, int Block, string RussianName, string Keywords, bool Emoji, int EmojiOrder)
{
    public string Preview => Category[0] == 'M' ? "◌" + Text : Category[0] == 'Z' ? "␣" : Category == "Cf" ? "◌" : Text;
    public string Code => string.Join(" ", Text.EnumerateRunes().Select(r => "U+" + r.Value.ToString("X4", CultureInfo.InvariantCulture)));
    public string Hint => (RussianName.Length > 0 ? RussianName + "\n" : "") + Name + "\n" + Code;
}

internal sealed class OfficeUnicodeCatalog
{
    internal const string Component = "office.unicode-data";
    private sealed record Manifest(string Version, int Count, int Emoji, OfficeUnicodeBlock[] Blocks, PinnedTreeFile[] Files);
    private static readonly object Gate = new();
    private static Task<OfficeUnicodeCatalog>? _loading;
    internal string Version { get; }
    internal OfficeUnicodeBlock[] Blocks { get; }
    internal OfficeUnicodeSymbol[] Symbols { get; }
    internal int EmojiCount { get; }
    private readonly Lazy<OfficeSymbolSuggestions> _suggestions;
    internal OfficeSymbolSuggestions Suggestions => _suggestions.Value;

    private OfficeUnicodeCatalog(Manifest manifest, OfficeUnicodeSymbol[] symbols)
    { Version = manifest.Version; Blocks = manifest.Blocks; Symbols = symbols; EmojiCount = manifest.Emoji;
        _suggestions = new(() => new OfficeSymbolSuggestions(symbols)); }

    internal static Task<OfficeUnicodeCatalog> LoadAsync(CancellationToken token)
    {
        lock (Gate)
        {
            if (_loading == null || _loading.IsFaulted || _loading.IsCanceled) _loading = LoadCoreAsync();
            return _loading.WaitAsync(token);
        }
    }

    private static async Task<OfficeUnicodeCatalog> LoadCoreAsync()
    {
        using var audit = ComponentAudit.CaptureOperation();
        await using var resource = typeof(OfficeUnicodeCatalog).Assembly.GetManifestResourceStream("AIHub.OfficeUnicodeManifest")
            ?? throw new InvalidDataException("OfficeSymbols.DataMissing");
        var manifest = await JsonSerializer.DeserializeAsync<Manifest>(resource) ?? throw new InvalidDataException("OfficeSymbols.DataMissing");
        var directory = Path.Combine(AppContext.BaseDirectory, "Tools", "OfficeUnicode");
        await RuntimeFileAudit.VerifyAsync(Component, directory, manifest.Version, manifest.Files, CancellationToken.None);
        return await Task.Run(() =>
        {
            using var file = File.OpenRead(Path.Combine(directory, "characters.tsv.gz"));
            using var compressed = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new StreamReader(compressed, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
            var symbols = new List<OfficeUnicodeSymbol>(manifest.Count);
            while (reader.ReadLine() is { } line)
            {
                var f = line.Split('\t');
                if (f.Length != 9) throw new InvalidDataException("OfficeSymbols.DataMissing");
                symbols.Add(new(int.Parse(f[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture), f[1], f[2], string.Intern(f[3]),
                    int.Parse(f[4], CultureInfo.InvariantCulture), f[5], f[6], f[7] == "1", int.Parse(f[8], CultureInfo.InvariantCulture)));
            }
            if (symbols.Count != manifest.Count) throw new InvalidDataException("OfficeSymbols.DataMissing");
            return new OfficeUnicodeCatalog(manifest, symbols.ToArray());
        });
    }

    internal OfficeUnicodeSymbol[] Search(string query, OfficeSymbolScope scope, int block, CancellationToken token)
    {
        query = query.Trim();
        var literal = CodeQuery(query);
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(TranslateKeyword).ToArray();
        var result = new List<OfficeUnicodeSymbol>();
        foreach (var symbol in Symbols)
        {
            token.ThrowIfCancellationRequested();
            if (block >= 0 && symbol.Block != block || !InScope(symbol, scope)) continue;
            if (literal != null ? symbol.Text != literal : query.Length > 0 && !symbol.Text.Contains(query, StringComparison.Ordinal)
                && !words.All(word => symbol.Name.Contains(word, StringComparison.OrdinalIgnoreCase)
                    || symbol.RussianName.Contains(word, StringComparison.OrdinalIgnoreCase) || symbol.Keywords.Contains(word, StringComparison.OrdinalIgnoreCase)
                    || Blocks[symbol.Block].Name.Contains(word, StringComparison.OrdinalIgnoreCase))) continue;
            result.Add(symbol);
        }
        return scope == OfficeSymbolScope.Emoji ? result.OrderBy(s => s.EmojiOrder).ToArray() : result.ToArray();
    }

    private bool InScope(OfficeUnicodeSymbol symbol, OfficeSymbolScope scope) => scope switch
    {
        OfficeSymbolScope.Emoji => symbol.Emoji,
        OfficeSymbolScope.Arrows => symbol.Name.Contains("ARROW", StringComparison.Ordinal),
        OfficeSymbolScope.Signs => symbol.Category[0] is 'S' or 'P',
        OfficeSymbolScope.Math => symbol.Category == "Sm" || Blocks[symbol.Block].Name.Contains("Mathematical", StringComparison.Ordinal),
        OfficeSymbolScope.Currency => symbol.Category == "Sc" || symbol.First is 0xA9 or 0xAE or 0x2122 or 0xB0 or 0xA7,
        OfficeSymbolScope.Borders => Blocks[symbol.Block].Name is "Box Drawing" or "Block Elements" or "Geometric Shapes" or "Geometric Shapes Extended",
        OfficeSymbolScope.Letters => symbol.Category[0] == 'L',
        OfficeSymbolScope.Marks => symbol.Category[0] == 'M',
        _ => true
    };

    private static string TranslateKeyword(string word)
    {
        var lower = word.ToLowerInvariant();
        foreach (var (prefix, english) in new[] { ("стрел", "arrow"), ("сердц", "heart"), ("звёзд", "star"), ("звезд", "star"),
            ("галоч", "check"), ("бесконеч", "infinity"), ("рубл", "ruble"), ("евро", "euro"), ("рамк", "box drawing") })
            if (lower.StartsWith(prefix, StringComparison.Ordinal)) return english;
        return word;
    }

    private static string? CodeQuery(string query)
    {
        if (!query.StartsWith("U+", StringComparison.OrdinalIgnoreCase)
            && !(query.Length >= 4 && query.Any(char.IsAsciiDigit) && query.All(c => char.IsAsciiHexDigit(c) || c == ' '))) return null;
        var codes = Regex.Split(query.Replace("U+", "", StringComparison.OrdinalIgnoreCase).Trim(), @"\s+");
        var text = new StringBuilder();
        foreach (var code in codes)
        {
            if (!int.TryParse(code, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) || !Rune.IsValid(value)) return "";
            text.Append(char.ConvertFromUtf32(value));
        }
        return text.ToString();
    }
}
