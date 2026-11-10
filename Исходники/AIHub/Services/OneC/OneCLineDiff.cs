namespace AIHub.Services.OneC;

internal sealed record OneCLineChanges(int[] Removed, int[] Added);
internal static class OneCLineDiff
{
    internal static OneCLineChanges Compare(string source, string result)
    {
        var a = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var b = result.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var removed = new List<int>(); var added = new List<int>();
        if ((long)a.Length * b.Length > 160000)
        {
            var start = 0; while (start < Math.Min(a.Length, b.Length) && a[start] == b[start]) start++;
            var endA = a.Length; var endB = b.Length;
            while (endA > start && endB > start && a[endA - 1] == b[endB - 1]) { endA--; endB--; }
            return new(Enumerable.Range(start + 1, endA - start).ToArray(), Enumerable.Range(start + 1, endB - start).ToArray());
        }
        var lengths = new int[a.Length + 1, b.Length + 1];
        for (var x = a.Length - 1; x >= 0; x--)
            for (var y = b.Length - 1; y >= 0; y--)
                lengths[x, y] = a[x] == b[y] ? lengths[x + 1, y + 1] + 1 : Math.Max(lengths[x + 1, y], lengths[x, y + 1]);
        var i = 0; var j = 0;
        while (i < a.Length || j < b.Length)
        {
            if (i < a.Length && j < b.Length && a[i] == b[j]) { i++; j++; }
            else if (i < a.Length && (j == b.Length || lengths[i + 1, j] >= lengths[i, j + 1])) removed.Add(++i);
            else added.Add(++j);
        }
        return new(removed.ToArray(), added.ToArray());
    }
}
