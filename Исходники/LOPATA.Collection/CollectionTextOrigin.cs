using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Lopata.Collection;

internal static class CollectionTextOrigin
{
    public static CollectionOrigin? Read(Stream input, string extension)
    {
        using var reader = new StreamReader(input, Encoding.UTF8, true, 4096, true);
        var buffer = new char[Math.Min(CollectionOriginReader.MetadataLimit, 256 * 1024)];
        var count = reader.ReadBlock(buffer, 0, buffer.Length);
        var text = new string(buffer, 0, count);
        if (extension == ".rtf") return Rtf(text);
        if (extension == ".csv")
        {
            var line = text.Split('\n', 2)[0].TrimStart('\uFEFF').TrimEnd('\r');
            return line.StartsWith("# LOPATA ", StringComparison.Ordinal) ? CollectionOriginReader.Json(line[9..]) : null;
        }
        if (extension is ".txt" or ".md")
        {
            var line = text.Split('\n', 2)[0].TrimStart('\uFEFF');
            var prefix = extension == ".md" ? "<!-- LOPATA.Provenance=" : "[LOPATA.Provenance=";
            var suffix = extension == ".md" ? " -->" : "]";
            if (line.StartsWith(prefix, StringComparison.Ordinal) && line.TrimEnd().EndsWith(suffix, StringComparison.Ordinal))
                return CollectionOriginReader.Json(line.TrimEnd()[prefix.Length..^suffix.Length]);
            return null;
        }
        var match = Regex.Match(text, "<meta\\s+[^>]*name=[\"']LOPATA\\.Provenance[\"'][^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (!match.Success) return null;
        var content = Regex.Match(match.Value, "\\bcontent=(?:\"([^\"]*)\"|'([^']*)')", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (!content.Success) return null;
        return CollectionOriginReader.Json(WebUtility.HtmlDecode(content.Groups[1].Success ? content.Groups[1].Value : content.Groups[2].Value));
    }

    private static CollectionOrigin? Rtf(string text)
    {
        if (!text.StartsWith("{\\rtf", StringComparison.Ordinal)) return null;
        const string marker = "{\\propname LOPATA.Provenance}\\proptype30{\\staticval ";
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;
        start += marker.Length;
        var result = new StringBuilder();
        for (var pos = start; pos < text.Length && result.Length < 128 * 1024; pos++)
        {
            var character = text[pos];
            if (character == '}') return CollectionOriginReader.Json(result.ToString());
            if (character == '\\')
            {
                if (++pos >= text.Length) return null;
                character = text[pos];
                if (character is not ('\\' or '{' or '}')) return null;
            }
            result.Append(character);
        }
        return null;
    }

    public static CollectionOrigin? Pdf(Stream input)
    {
        // XMP is a metadata stream. Inspect bounded head and tail only, never all document text.
        foreach (var offset in new[] { 0L, Math.Max(0L, input.Length - CollectionOriginReader.MetadataLimit) }.Distinct())
        {
            input.Position = offset;
            var bytes = new byte[(int)Math.Min(input.Length - offset, CollectionOriginReader.MetadataLimit)];
            input.ReadExactly(bytes);
            var text = Encoding.UTF8.GetString(bytes);
            var start = text.IndexOf("<x:xmpmeta", StringComparison.Ordinal);
            var end = text.IndexOf("</x:xmpmeta>", start < 0 ? 0 : start, StringComparison.Ordinal);
            if (start >= 0 && end > start)
                return CollectionOriginReader.Xml(new MemoryStream(Encoding.UTF8.GetBytes(text[start..(end + 12)])));
        }
        return null;
    }
}
