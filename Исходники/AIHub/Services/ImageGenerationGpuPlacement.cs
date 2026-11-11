using System.Text.RegularExpressions;

namespace AIHub.Services;

internal enum ImageGenerationGpuPlacement
{
    Unknown,
    Vram,
    Hybrid,
    GpuUnavailable,
    MemoryUnavailable
}

internal static partial class ImageGenerationPlacementLog
{
    // Format emitted by the auto-fit planner in the pinned sd.cpp build.
    [GeneratedRegex("""auto-fit: --backend "([^"]+)"(?: --params-backend "([^"]+)")?""",
        RegexOptions.CultureInvariant)]
    private static partial Regex Plan();

    internal static ImageGenerationGpuPlacement Read(string line)
    {
        if (line.Contains("auto-fit: no GPU devices", StringComparison.Ordinal))
            return ImageGenerationGpuPlacement.GpuUnavailable;
        if (line.Contains("auto-fit: no GPU memory budget available", StringComparison.Ordinal))
            return ImageGenerationGpuPlacement.MemoryUnavailable;

        var plan = Plan().Match(line);
        if (!plan.Success) return ImageGenerationGpuPlacement.Unknown;

        static IEnumerable<string> Devices(string assignment) => assignment
            .Split([',', '&'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part[(part.IndexOf('=') + 1)..]);

        var compute = Devices(plan.Groups[1].Value).ToArray();
        if (compute.Length == 0 || compute.All(device => device is "cpu" or "(default)")
            || plan.Groups[1].Value.Split(',').Any(part => part.Trim() == "diffusion=cpu"))
            return ImageGenerationGpuPlacement.GpuUnavailable;

        var parameters = Devices(plan.Groups[2].Value).ToArray();
        if (parameters.Contains("disk")) return ImageGenerationGpuPlacement.MemoryUnavailable;
        return parameters.Contains("cpu") || compute.Contains("cpu")
            ? ImageGenerationGpuPlacement.Hybrid : ImageGenerationGpuPlacement.Vram;
    }
}
