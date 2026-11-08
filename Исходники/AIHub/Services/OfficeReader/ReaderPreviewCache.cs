using System.IO;
using System.Security.Cryptography;

namespace AIHub.Services.OfficeReader;

// Session-only previews: no persistent copy of the user's document is retained.
internal static class ReaderPreviewCache
{
    private const long Capacity = 96 * 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (byte[] Pdf, long Used)> Entries = new();
    private static long _clock;

    internal static async Task<string?> KeyAsync(string source, string extension, CancellationToken token)
    {
        // Markup can refer to files that change independently of the source.
        if (extension is ".pdf" or ".md" or ".markdown" or ".html" or ".htm") return null;
        await using var stream = File.OpenRead(source);
        return extension + ":" + Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }

    internal static async Task<bool> RestoreAsync(string? key, string target, CancellationToken token)
    {
        if (key is null) return false;
        byte[] pdf;
        lock (Gate)
        {
            if (!Entries.TryGetValue(key, out var entry)) return false;
            pdf = entry.Pdf; Entries[key] = (pdf, ++_clock);
        }
        await File.WriteAllBytesAsync(target, pdf, token);
        return true;
    }

    internal static async Task StoreAsync(string? key, string pdf, CancellationToken token)
    {
        if (key is null || new FileInfo(pdf).Length > Capacity / 3) return;
        var bytes = await File.ReadAllBytesAsync(pdf, token);
        if (bytes.Length == 0 || bytes.Length > Capacity / 3) return;
        lock (Gate)
        {
            Entries[key] = (bytes, ++_clock);
            while (Entries.Count > 8 || Entries.Values.Sum(entry => (long)entry.Pdf.Length) > Capacity)
                Entries.Remove(Entries.MinBy(entry => entry.Value.Used).Key);
        }
    }
}
