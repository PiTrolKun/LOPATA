using System.Text;
using System.Text.RegularExpressions;

namespace AIHub.Services.LiteraryImport;

/// <summary>Suggest aliases, never infer that two similar titles are the same book.</summary>
public static class ImportWorkNames
{
    public static string Key(string title) => string.Join(" ", Regex.Matches(title.Normalize(NormalizationForm.FormKC).ToLowerInvariant().Replace('ё','е'), @"[\p{L}\p{N}]+")
        .Select(m => m.Value));
    public static bool Similar(string left, string right)
    {
        var a = Key(left).Split(' '); var b = Key(right).Split(' ');
        if (a.SequenceEqual(b)) return true;
        if (a.Length < 3 || a.Length != b.Length) return false;
        var changed = 0;
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] == b[i]) continue;
            if (++changed > 2 || Math.Min(a[i].Length,b[i].Length) < 4 || a[i].Any(char.IsDigit) || b[i].Any(char.IsDigit)) return false;
            if (Distance(a[i],b[i]) > 2) return false;
        }
        return true;
    }
    private static int Distance(string a, string b)
    {
        if (Math.Abs(a.Length-b.Length)>2) return 3;
        var row = Enumerable.Range(0,b.Length+1).ToArray();
        for (var i=0;i<a.Length;i++)
        {
            var diagonal=row[0]; row[0]=i+1;
            for (var j=0;j<b.Length;j++) { var old=row[j+1]; row[j+1]=Math.Min(Math.Min(row[j]+1,old+1),diagonal+(a[i]==b[j]?0:1)); diagonal=old; }
        }
        return row[^1];
    }
    public static ImportDecision[] Merge(ImportDecision[] decisions, IEnumerable<string> names, string target)
    {
        target=target.Trim();
        if (target.Length is 0 or >150) throw new ArgumentException("Literary.Import.MetadataRequired");
        var selected=names.ToHashSet(StringComparer.Ordinal);
        return decisions.Select(d=>selected.Contains(d.Project)?d with { Project=target }:d).ToArray();
    }
}
