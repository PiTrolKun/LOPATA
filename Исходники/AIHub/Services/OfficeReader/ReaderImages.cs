using System.IO;
using SharpCompress.Archives;
using SkiaSharp;

namespace AIHub.Services.OfficeReader;

internal sealed class ReaderImages : IReaderPages
{
    private readonly string[] _files;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public IReadOnlyList<ReaderPage> Pages { get; }
    internal static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tif", ".tiff", ".gif"];

    internal ReaderImages(IEnumerable<string> files)
    {
        _files = files.ToArray();
        Pages = _files.Select(path =>
        {
            using var codec = SKCodec.Create(path);
            if (codec is null || codec.Info.Width < 1 || codec.Info.Height < 1 || (long)codec.Info.Width * codec.Info.Height > 100_000_000)
                throw new InvalidDataException("Reader.InvalidImage");
            return new ReaderPage(codec.Info.Width, codec.Info.Height);
        }).ToArray();
        if (_files.Length == 0) throw new InvalidDataException("Reader.Empty");
    }

    internal static ReaderImages Comic(string path, string workspace, CancellationToken token)
    {
        using var archive = ArchiveFactory.Open(path);
        var entries = archive.Entries.Where(e => !e.IsDirectory && Extensions.Contains(Path.GetExtension(e.Key ?? "").ToLowerInvariant()))
            .OrderBy(e => e.Key, NaturalNameComparer.Instance).ToArray();
        var files = new List<string>(); long total = 0;
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry.IsEncrypted || entry.Size > 128 * 1024 * 1024 || files.Count >= 20000) throw new InvalidDataException("Reader.ArchiveLimit");
            var output = Path.Combine(workspace, "image-" + files.Count + Path.GetExtension(entry.Key));
            using var input = entry.OpenEntryStream(); using var stream = File.Create(output);
            var buffer = new byte[65536]; long size = 0; int count;
            while ((count = input.Read(buffer)) > 0)
            {
                token.ThrowIfCancellationRequested(); size += count; total += count;
                if (size > 128 * 1024 * 1024 || total > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Reader.ArchiveLimit");
                stream.Write(buffer, 0, count);
            }
            files.Add(output);
        }
        return new(files);
    }

    public async Task<string> RenderAsync(int page, int width, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { return await Task.Run(() =>
        {
        token.ThrowIfCancellationRequested();
        var target = Path.Combine(Path.GetDirectoryName(_files[page])!, $"raster-{page}-{width}.png");
        if (File.Exists(target)) return target;
        using var image = SKImage.FromEncodedData(_files[page]);
        var scale = Math.Min(1, Math.Min((double)width / image.Width, 4096d / image.Height));
        using var bitmap = new SKBitmap(Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale)));
        using (var canvas = new SKCanvas(bitmap))
        { canvas.Clear(SKColors.White); canvas.DrawImage(image, new SKRect(0, 0, bitmap.Width, bitmap.Height), new SKSamplingOptions(SKFilterMode.Linear)); }
        using var result = SKImage.FromBitmap(bitmap); using var data = result.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(target); data.SaveTo(stream); return target;
        }, token); }
        finally { _gate.Release(); }
    }
    public async ValueTask DisposeAsync() { await _gate.WaitAsync(); _gate.Release(); }
}

internal sealed class NaturalNameComparer : IComparer<string?>
{
    internal static readonly NaturalNameComparer Instance = new();
    public int Compare(string? left, string? right)
    {
        left ??= ""; right ??= ""; int a = 0, b = 0;
        while (a < left.Length && b < right.Length)
        {
            if (char.IsAsciiDigit(left[a]) && char.IsAsciiDigit(right[b]))
            {
                var aa = a; var bb = b;
                while (a < left.Length && char.IsAsciiDigit(left[a])) a++;
                while (b < right.Length && char.IsAsciiDigit(right[b])) b++;
                var x = left[aa..a].TrimStart('0'); var y = right[bb..b].TrimStart('0');
                var result = x.Length.CompareTo(y.Length); if (result != 0) return result;
                result = string.Compare(x, y, StringComparison.Ordinal); if (result != 0) return result;
            }
            else { var result = char.ToUpperInvariant(left[a++]).CompareTo(char.ToUpperInvariant(right[b++])); if (result != 0) return result; }
        }
        var end = (left.Length - a).CompareTo(right.Length - b);
        return end != 0 ? end : string.Compare(left, right, StringComparison.Ordinal);
    }
}
