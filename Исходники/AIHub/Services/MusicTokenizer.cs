using System.IO;
using System.Text;

namespace AIHub.Services;

public interface IMusicTokenizer { int Count(string text, CancellationToken cancellation = default); }

// YuE2 protocol adaptation; yue2.cpp authors, MIT (Licenses/texts/music-tokenizer-MIT.txt).
// Ordinary NFC byte-pair encoding; token-looking user text never becomes a special token.
public sealed class MusicTokenizer : IMusicTokenizer
{
    private readonly Dictionary<string, int> _vocabulary, _ranks;
    private static readonly string[] ByteSymbols = CreateByteSymbols();
    public MusicTokenizer(MusicTokenizerMetadata metadata)
    {
        _vocabulary = metadata.Tokens.Select((text, index) => (text, index)).ToDictionary(p => p.text, p => p.index, StringComparer.Ordinal);
        _ranks = metadata.Merges.Select((text, index) => (text, index)).ToDictionary(p => p.text, p => p.index, StringComparer.Ordinal);
        if (ByteSymbols.Any(s => !_vocabulary.ContainsKey(s))) throw new InvalidDataException("Incomplete tokenizer byte vocabulary.");
    }
    public int Count(string text, CancellationToken cancellation = default) => Encode(text, cancellation).Length;
    public int[] Encode(string text, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var result = new List<int>();
        foreach (var piece in MusicTextPieces.Split(text.Normalize(NormalizationForm.FormC), cancellation))
            EncodePiece(piece, result, cancellation);
        return result.ToArray();
    }
    private void EncodePiece(string piece, List<int> result, CancellationToken cancellation)
    {
        var bytes = Encoding.UTF8.GetBytes(piece);
        var nodes = bytes.Select((b, i) => new Node(ByteSymbols[b], i - 1, i + 1 < bytes.Length ? i + 1 : -1)).ToArray();
        var queue = new PriorityQueue<(int Left, int Right, int LeftVersion, int RightVersion), (int Rank, int Position)>();
        for (var i = 0; i + 1 < nodes.Length; i++) Enqueue(i);
        var iteration = 0;
        while (queue.TryDequeue(out var pair, out _))
        {
            if ((iteration++ & 63) == 0) cancellation.ThrowIfCancellationRequested();
            var left = nodes[pair.Left]; var right = nodes[pair.Right];
            if (left.Version != pair.LeftVersion || right.Version != pair.RightVersion || left.Next != pair.Right || right.Removed) continue;
            left.Text += right.Text; left.Next = right.Next; left.Version++; right.Removed = true; right.Version++;
            if (left.Next >= 0) nodes[left.Next].Previous = pair.Left;
            if (left.Previous >= 0) Enqueue(left.Previous);
            Enqueue(pair.Left);
        }
        for (var i = 0; i >= 0 && i < nodes.Length; i = nodes[i].Next)
            result.Add(_vocabulary.TryGetValue(nodes[i].Text, out var id) ? id : throw new InvalidDataException("Tokenizer merge has no vocabulary entry."));
        void Enqueue(int index)
        {
            var left = nodes[index]; if (left.Next < 0) return;
            var right = nodes[left.Next];
            if (_ranks.TryGetValue(left.Text + " " + right.Text, out var rank))
                queue.Enqueue((index, left.Next, left.Version, right.Version), (rank, index));
        }
    }
    private sealed class Node(string text, int previous, int next)
    {
        public string Text = text;
        public int Previous = previous, Next = next, Version;
        public bool Removed;
    }
    internal static string[] CreateByteSymbols()
    {
        var next = 256; var result = new string[256];
        for (var b = 0; b < 256; b++)
            result[b] = char.ConvertFromUtf32(b is >= 33 and <= 126 or >= 161 and <= 172 or >= 174 and <= 255 ? b : next++);
        return result;
    }
}
