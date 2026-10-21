namespace AIHub.Services.MusicLora;

public sealed record MusicLoraMemoryEstimate(long AdditionalBytes, long? FreeBytes, long? BaselineBytes, bool? GpuFits);
public static class MusicLoraForecast
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset Time, long Free)> Observations = new();
    public static void ObserveFreeGpu(string variation, long free) { if (free > 0) Observations[variation] = (DateTimeOffset.UtcNow, free); }
    public static long? RecentFreeGpu(string variation) => Observations.TryGetValue(variation, out var value)
        && DateTimeOffset.UtcNow - value.Time < TimeSpan.FromMinutes(3) ? value.Free : null;
    // The pinned XL checkpoints are F32; the official GPU pipeline loads BF16.
    // Add an intentionally coarse VAE/conditioning/working reserve, not a hardware promise.
    public static long AceBaseline(string root, int duration) => checked(MusicAceCatalog.Cards(root).Single(c => c.ModelArtifactId == MusicAceCatalog.Variation)
        .Files.Where(f => f.RelativePath.EndsWith(".safetensors", StringComparison.Ordinal)).Sum(f => f.SizeBytes) / 2
        + 2L * 1024 * 1024 * 1024 + duration * 2L * 1024 * 1024);
    // Adapters may be expanded to working precision and coexist with merge scratch buffers.
    public static MusicLoraMemoryEstimate Estimate(IReadOnlyList<MusicLoraUse> uses, long? free, long? baseline)
    {
        var bytes = uses.Count == 0 ? 0 : checked(128L * 1024 * 1024 + uses.Sum(u => MusicLoraCatalog.Find(u.Id).Bytes) * 3);
        return new(bytes, free, baseline, free is > 0 && baseline is > 0 ? baseline + bytes <= free : null);
    }
    public static string StatusKey(MusicLoraMemoryEstimate estimate) => estimate.AdditionalBytes == 0 ? "NoAdditional"
        : estimate.GpuFits switch {
            false when estimate.BaselineBytes.GetValueOrDefault() > estimate.FreeBytes.GetValueOrDefault() => "BaselineLow",
            true => "Fits", false => "Low", _ => "Unknown"
        };
}
