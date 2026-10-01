using System.IO;
using AIHub.Models;

namespace AIHub.Services;

public static class FinancialModelDiscovery
{
    public static IReadOnlyList<DebugModelInfo> Discover(StorageSettings settings)
    {
        var models = new DebugModelDiscoveryService().Discover(settings).Where(IsSupportedText).ToList();
        // BIN is admitted only through a known installed chatllm card, not arbitrary weights.
        foreach (var card in new ManagedModelLibraryStore().LoadAll().Where(c => c.RuntimeBackend.Contains("chatllm", StringComparison.OrdinalIgnoreCase)))
        foreach (var file in card.Files.Where(f => f.RelativePath.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)))
        {
            var path = Path.Combine(card.InstallDirectory, file.RelativePath);
            if (File.Exists(path)) models.Add(new() { Name = card.DisplayName, Path = path, Format = "chatllm", IsRunnable = true, SizeBytes = new FileInfo(path).Length });
        }
        return models.DistinctBy(m => m.Path, StringComparer.OrdinalIgnoreCase).OrderByDescending(m => m.IsCoreModel).ThenBy(m => m.Name).ToArray();
    }
    public static bool IsSupportedText(DebugModelInfo model)
    {
        if (!model.IsRunnable || !model.Format.Equals("gguf", StringComparison.OrdinalIgnoreCase)) return false;
        var name = Path.GetFileName(model.Path).ToLowerInvariant();
        if (new[] { "mmproj", "embedding", "embed", "reranker", "bge-", "e5-", "whisper" }.Any(name.Contains)) return false;
        try
        {
            using var stream = File.OpenRead(model.Path); var header = new byte[Math.Min(stream.Length, 1024 * 1024)]; stream.ReadExactly(header);
            var arch = GgufMetadataReader.Read(header).Architecture.ToLowerInvariant();
            return !GgufMetadataReader.IsKnownUnsupportedArchitecture(arch) && !new[] { "bert", "t5", "clip", "wavtokenizer", "whisper" }.Any(arch.Contains);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return false; }
    }
}
