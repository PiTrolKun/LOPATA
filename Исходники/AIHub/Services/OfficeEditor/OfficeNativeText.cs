using System.Xml.Linq;

namespace AIHub.Services.OfficeEditor;

internal static class OfficeNativeText
{
    // Apply the changed span without moving the unchanged characters out of their
    // original runs. A suffix inside a link, for example, keeps that link.
    internal static void Replace(IReadOnlyList<XText> runs, string value)
    {
        if (runs.Count == 0) throw new ArgumentException("The text block has no editable run.", nameof(runs));
        var old = string.Concat(runs.Select(run => run.Value));
        if (old == value) return;
        var prefix = 0;
        var common = Math.Min(old.Length, value.Length);
        while (prefix < common && old[prefix] == value[prefix]) prefix++;
        if (prefix > 0 && char.IsHighSurrogate(old[prefix - 1])) prefix--;
        var suffix = 0;
        while (suffix < common - prefix && old[^(suffix + 1)] == value[^(suffix + 1)]) suffix++;
        if (suffix > 0 && char.IsLowSurrogate(old[^suffix])) suffix--;
        var end = old.Length - suffix;
        var insertion = value.Substring(prefix, value.Length - suffix - prefix);
        var offset = 0;
        var inserted = false;
        for (var i = 0; i < runs.Count; i++)
        {
            var run = runs[i];
            var text = run.Value;
            var after = offset + text.Length;
            var left = Math.Clamp(prefix - offset, 0, text.Length);
            var right = Math.Clamp(end - offset, 0, text.Length);
            var ownsInsertion = !inserted && (prefix < after || prefix == after && (prefix == end || i == runs.Count - 1));
            var replacement = text[..left] + (ownsInsertion ? insertion : "") + text[right..];
            if (text != replacement) run.Value = replacement;
            inserted |= ownsInsertion;
            offset = after;
        }
    }
}
