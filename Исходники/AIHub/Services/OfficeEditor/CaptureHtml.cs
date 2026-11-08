using System.Text;
using System.Text.RegularExpressions;

namespace AIHub.Services.OfficeEditor;

internal static class CaptureHtml
{
    internal static string? Fragment(string? clipboard)
    {
        if (string.IsNullOrWhiteSpace(clipboard)) return null;
        var begin = clipboard.IndexOf("<!--StartFragment-->", StringComparison.OrdinalIgnoreCase);
        var end = clipboard.IndexOf("<!--EndFragment-->", StringComparison.OrdinalIgnoreCase);
        string? fragment = begin >= 0 && end > begin ? clipboard[(begin + 20)..end] : null;
        if (fragment is null)
        {
            var start = Regex.Match(clipboard, @"StartFragment:(\d+)"); var stop = Regex.Match(clipboard, @"EndFragment:(\d+)");
            var bytes = Encoding.UTF8.GetBytes(clipboard);
            if (!start.Success || !stop.Success || !int.TryParse(start.Groups[1].Value, out var a) || !int.TryParse(stop.Groups[1].Value, out var b)
                || a < 0 || b < a || b > bytes.Length) return null;
            fragment = Encoding.UTF8.GetString(bytes, a, b - a);
        }
        // Preserve text styling and links; executable content is not a capture format.
        fragment = Regex.Replace(fragment, @"<(script|iframe|object|embed)\b[^>]*>.*?</\1\s*>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        fragment = Regex.Replace(fragment, @"\s+on\w+\s*=\s*('.*?'|"".*?""|[^\s>]+)", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return fragment;
    }
}
