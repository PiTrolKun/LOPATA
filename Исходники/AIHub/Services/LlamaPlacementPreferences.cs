using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIHub.Services;

internal sealed class LlamaPlacementPreferences(string directory)
{
    internal static LlamaPlacementPreferences Default { get; } = new(Path.Combine(AppDataPaths.BaseDirectory, "Runtime", "LlamaPlacement"));
    internal static string Key(string modelPath, int requestedContext, LlamaRuntimeSelection selection)
    {
        var file = new FileInfo(modelPath);
        var identity = JsonSerializer.Serialize(new { revision = 4, path = file.FullName.ToUpperInvariant(), file.Length,
            modified = file.LastWriteTimeUtc.Ticks, requestedContext, selection.Bundle.Directory,
            selection.Bundle.EngineSetId, runtimeVersion = selection.Bundle.Version, runtimeRevision = selection.Bundle.Revision,
            selection.Bundle.Backend, device = selection.Device?.Id, name = selection.Device?.Name,
            totalBytes = selection.Device?.TotalBytes });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
    internal LlamaStartupPlacement? Load(string key, int blockCount, int requestedContext)
    {
        var path = PathFor(key);
        if (!File.Exists(path)) return null;
        var placement = JsonSerializer.Deserialize<LlamaStartupPlacement>(File.ReadAllBytes(path));
        if (placement is null || placement.ContextTokens < 1024 || placement.ContextTokens > requestedContext
            || !Enum.IsDefined(placement.Mode)
            || placement.Mode == LlamaPlacementMode.Gpu && placement.GpuLayers != blockCount + 1
            || placement.Mode == LlamaPlacementMode.Hybrid && (placement.GpuLayers <= 0 || placement.GpuLayers >= blockCount)
            || placement.Mode == LlamaPlacementMode.Cpu && placement.GpuLayers != 0)
            throw new InvalidDataException("Invalid saved llama placement.");
        return placement;
    }
    internal void Save(string key, LlamaStartupPlacement placement)
    {
        Directory.CreateDirectory(directory);
        var destination = PathFor(key); var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(stream, placement); stream.Flush(true); }
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private string PathFor(string key)
    {
        if (key.Length != 64 || !key.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid placement key.");
        return Path.Combine(directory, key + ".json");
    }
}
