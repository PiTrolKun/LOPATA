using System.IO;
using System.Text.RegularExpressions;

namespace AIHub.Services;

/// <summary>Measured hints, never a claim of timbre transfer or guaranteed model compliance.</summary>
internal sealed record MusicStudioReferenceFacts(string Tempo, string Key, string Meter)
{
    public static MusicStudioReferenceFacts Read(string abc)
    {
        var tempo = Regex.Match(abc, @"(?m)^Q:\s*(?:1/4\s*=\s*)?(\d{2,3})\s*$");
        var key = Regex.Match(abc, @"(?m)^K:\s*([A-G][#b]?(?:m|min|maj)?)\s*$");
        var meter = Regex.Match(abc, @"(?m)^M:\s*(\d{1,2}/\d{1,2})\s*$");
        var normalizedKey = key.Groups[1].Value;
        if (normalizedKey.EndsWith("maj", StringComparison.Ordinal)) normalizedKey = normalizedKey[..^3];
        else if (normalizedKey.EndsWith("min", StringComparison.Ordinal)) normalizedKey = normalizedKey[..^3] + "m";
        var result = new MusicStudioReferenceFacts(tempo.Groups[1].Value.TrimStart('0'), normalizedKey, meter.Groups[1].Value);
        if (result.Tempo.Length + result.Key.Length + result.Meter.Length == 0)
            throw new InvalidDataException("Music.Transform.ReferenceAnalysisFailed");
        return result;
    }

    public static string Combine(IReadOnlyList<MusicStudioReferenceFacts> facts)
    {
        // Do not average incompatible keys/meters or send multiple contradictory tempos.
        string Single(Func<MusicStudioReferenceFacts, string> select)
        {
            var values = facts.Select(select).Where(v => v.Length > 0).Distinct().ToArray();
            if (values.Length > 1) throw new InvalidDataException("Music.Transform.ConflictingStudioReferences");
            return values.FirstOrDefault() ?? "";
        }
        var tempo = Single(f => f.Tempo); var key = Single(f => f.Key); var meter = Single(f => f.Meter);
        return string.Join(", ", new[] { tempo.Length > 0 ? tempo + " BPM" : "", key, meter }.Where(v => v.Length > 0));
    }
}
