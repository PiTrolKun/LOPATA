using System.Globalization;
using System.Text.RegularExpressions;

namespace AIHub.Services;

/// <summary>Native allocation intent catches WDDM paging before resident VRAM counters reach saturation.</summary>
internal sealed partial class LlamaGpuAllocationTracker(string deviceId, long baselineBytes)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _buffers = [];
    private string _cache = "ordinary";
    [GeneratedRegex(@"(?<device>(?:Vulkan|CUDA|HIP|SYCL)\d+)\s+(?<kind>model|KV|RS|compute|output) buffer size\s*=\s*(?<size>[0-9]+(?:\.[0-9]+)?) MiB", RegexOptions.CultureInvariant)]
    private static partial Regex BufferLine();
    [GeneratedRegex(@"creating\s+SWA KV cache", RegexOptions.CultureInvariant)]
    private static partial Regex SwaLine();
    internal void Observe(string line)
    {
        lock (_gate)
        {
            if (line.Contains("creating non-SWA KV cache", StringComparison.Ordinal)) _cache = "global";
            else if (SwaLine().IsMatch(line)) _cache = "swa";
            var match = BufferLine().Match(line);
            if (!match.Success || match.Groups["device"].Value != deviceId
                || !decimal.TryParse(match.Groups["size"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var size)
                || size < 0 || size > long.MaxValue / 1048576) return;
            var kind = match.Groups["kind"].Value;
            var key = kind == "KV" ? "KV:" + _cache : kind;
            // Repeated graph-reserve logs describe the same buffer; keep its largest reported size.
            _buffers[key] = Math.Max(_buffers.GetValueOrDefault(key), (long)Math.Ceiling(size * 1048576));
        }
    }
    internal long IntendedUsedBytes
    {
        get
        {
            lock (_gate)
            {
                var total = Math.Max(0, baselineBytes);
                foreach (var bytes in _buffers.Values) total = bytes > long.MaxValue - total ? long.MaxValue : total + bytes;
                return total;
            }
        }
    }
}
