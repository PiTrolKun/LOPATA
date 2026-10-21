using System.Collections.Concurrent;
using System.Globalization;

namespace AIHub.Services.MusicLora;

/// <summary>Thread-safe snapshot of complete locally verified downloads, shared by search and download UI.</summary>
internal sealed class MusicLoraSearchExclusions
{
    private readonly ConcurrentDictionary<string, byte> _ids = new(), _payloads = new(), _singleWeights = new(StringComparer.Ordinal);
    internal MusicLoraSearchExclusions(IEnumerable<MusicLoraDefinition> installed) { foreach (var d in installed) Add(d); }
    private static string Payload(MusicLoraDefinition d) => MusicLoraSearchStore.Digest(d.Variation + "|" + d.Format + "|"
        + string.Join(";", d.Slots.OrderBy(p => p.Key).Select(p => p.Key + ":" + p.Value.ToString("R", CultureInfo.InvariantCulture)))
        + "|" + string.Join(";", d.Files.Select(f => f.Sha256).OrderBy(s => s, StringComparer.Ordinal)));
    internal void Add(MusicLoraDefinition d)
    {
        _ids[d.Id] = 0; _payloads[Payload(d)] = 0;
        if (d.Files.Length == 1) _singleWeights[d.Files[0].Sha256] = 0;
        foreach (var sha in MusicLoraUsageHints.EquivalentWeights(d)) _singleWeights[sha] = 0;
    }
    internal bool Include(MusicLoraDefinition d) => !_ids.ContainsKey(d.Id) && !_payloads.ContainsKey(Payload(d))
        && !(d.Files.Length == 1 && _singleWeights.ContainsKey(d.Files[0].Sha256));
    internal bool InspectWeight(string sha) => !_singleWeights.ContainsKey(sha);
}
