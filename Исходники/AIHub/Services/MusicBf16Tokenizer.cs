using System.IO;
using System.Text;

namespace AIHub.Services;

/// <summary>Checkpoint-native byte BPE; shares Qwen pre-tokenization, not GGUF storage.</summary>
public sealed class MusicBf16Tokenizer : IMusicTokenizer
{
    private readonly Dictionary<string, int> _ranks = new(StringComparer.Ordinal);
    public MusicBf16Tokenizer(string path, CancellationToken token = default)
    {
        foreach (var line in File.ReadLines(path)) {
            token.ThrowIfCancellationRequested(); if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            _ = Convert.FromBase64String(parts[0]); _ranks.Add(parts[0], int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
        }
        if (_ranks.Count != 151643) throw new InvalidDataException("Invalid YuE2 BF16 tokenizer.");
    }
    public int Count(string text, CancellationToken token = default) => Encode(text, token).Length;
    public int[] Encode(string text, CancellationToken token = default)
    {
        var result = new List<int>();
        foreach (var piece in MusicTextPieces.Split(text.Normalize(NormalizationForm.FormC), token)) {
            var bytes = Encoding.UTF8.GetBytes(piece);
            if (_ranks.TryGetValue(Convert.ToBase64String(bytes), out var whole)) { result.Add(whole); continue; }
            var segments = bytes.Select(b => new byte[] { b }).ToList();
            while (segments.Count > 1) {
                token.ThrowIfCancellationRequested(); var best = int.MaxValue; var at = -1;
                for (var i = 0; i < segments.Count - 1; i++) {
                    var candidate = Convert.ToBase64String([.. segments[i], .. segments[i + 1]]);
                    if (_ranks.TryGetValue(candidate, out var rank) && rank < best) { best = rank; at = i; }
                }
                if (at < 0) break;
                segments[at] = [.. segments[at], .. segments[at + 1]]; segments.RemoveAt(at + 1);
            }
            result.AddRange(segments.Select(b => _ranks[Convert.ToBase64String(b)]));
        }
        return result.ToArray();
    }
}
