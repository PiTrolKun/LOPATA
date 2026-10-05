using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace AIHub.Services;

/// <summary>Local, pinned runtime discovery. This never downloads or launches a binary.</summary>
public static class MusicYueRuntime
{
    public const string ComponentId = "runtime.music-yue2";
    public const string CudaComponentId = "runtime.music-yue2-cuda128";
    public const string VulkanComponentId = "runtime.music-yue2-vulkan";
    public const string CpuPack = "win-x64", CudaPack = "win-cuda128-x64";
    public const string Revision = "11c1ecb084329200e22fcb286e252b847442ea5c";
    public const string GgmlRevision = "40e16e4a814f7fe851a0c486fb9e8c722e957830";
    public static string DirectoryPath => File.Exists(Path.Combine(DirectoryForPack(CudaPack), "manifest.json"))
        ? DirectoryForPack(CudaPack) : DirectoryForPack(CpuPack);
    public static string DirectoryForPack(string pack)
    {
        if (pack is not (CpuPack or CudaPack)) throw new InvalidDataException("Unsupported YuE2 runtime pack.");
        var bundled = Path.Combine(AppContext.BaseDirectory, "MusicRuntime", pack);
        return File.Exists(Path.Combine(bundled, "manifest.json")) ? bundled
            : Path.Combine(AppDataPaths.BackendsDirectory, "yue2.cpp", Revision, pack);
    }
    public static bool IsCuda(string directory) => Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)) == CudaPack;
    private static readonly string[] RequiredFiles = ["yue-plan.exe", "yue-synth.exe", "yue-probe.exe", "ggml.dll", "ggml-base.dll", "ggml-cpu.dll"];

    public static async Task VerifyAsync(string directory, CancellationToken token)
    {
        using var input = File.OpenRead(Path.Combine(directory, "manifest.json"));
        using var manifest = await JsonDocument.ParseAsync(input, cancellationToken: token);
        var root = manifest.RootElement;
        if (root.GetProperty("SourceRevision").GetString() != Revision || root.GetProperty("GgmlRevision").GetString() != GgmlRevision
            || root.GetProperty("Architecture").GetString() != "win-x64"
            || root.GetProperty("Backend").GetString() != (IsCuda(directory) ? "CUDA 12.8 SM86 SM89 SM120 + Vulkan + CPU AVX2" : "CPU AVX2"))
            throw new InvalidDataException("Unsupported YuE2 runtime manifest.");
        var files = root.GetProperty("Files").EnumerateArray().ToArray();
        string[] required = IsCuda(directory) ? [.. RequiredFiles, "ggml-cuda.dll", "ggml-vulkan.dll", "cudart64_12.dll", "cublas64_12.dll", "cublasLt64_12.dll"] : RequiredFiles;
        if (files.Length != required.Length) throw new InvalidDataException("Incomplete YuE2 runtime manifest.");
        foreach (var name in required)
        {
            token.ThrowIfCancellationRequested();
            var entries = files.Where(f => f.GetProperty("Name").GetString() == name).ToArray();
            if (entries.Length != 1) throw new InvalidDataException("Duplicate or missing YuE2 runtime file.");
            using var file = File.OpenRead(Path.Combine(directory, name));
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, token));
            if (!hash.Equals(entries[0].GetProperty("Sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("YuE2 runtime checksum mismatch: " + name);
        }
    }
}
