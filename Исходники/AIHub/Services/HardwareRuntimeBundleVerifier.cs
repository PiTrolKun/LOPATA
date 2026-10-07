using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace AIHub.Services;

/// <summary>Trusted manifest and complete file integrity, before replacing an existing runtime.</summary>
public static class HardwareRuntimeBundleVerifier
{
    public static bool IsManaged(string id) => id is HardwareRuntimeCatalog.LlamaCpuId or HardwareRuntimeCatalog.LlamaVulkanId
        or HardwareRuntimeCatalog.SdCpuId or HardwareRuntimeCatalog.SdVulkanId;

    private static string ManifestHash(string id) => id switch
    {
        HardwareRuntimeCatalog.LlamaCpuId => "6a3d780bae6b8ccafce95ce62204108fef59426389ccc466fdbe3d91a3f14a69",
        HardwareRuntimeCatalog.LlamaVulkanId => "93125968a0af105bc951f56a4c239f329af46a2a3bad55aa1e3d44a40fff3924",
        HardwareRuntimeCatalog.SdCpuId => "c1ddf120c173d05bab8339e0f5041f58f28773a19cb81b3da65351c2b4431419",
        HardwareRuntimeCatalog.SdVulkanId => "13808f8ab7e86b2f56f787d2a84304634770195f8535d2b6cafbcab45416fa13",
        _ => throw new ArgumentException("Unknown hardware runtime.", nameof(id))
    };

    private static JsonDocument ReadTrustedManifest(string id, string directory)
    {
        var path = Path.Combine(directory, "runtime-manifest.json");
        Lopata.Updates.SafeUpdatePath.RejectLinks(path);
        if (!File.Exists(path) || new FileInfo(path).Length > 256 * 1024)
            throw new InvalidDataException("Hardware runtime manifest is missing or too large.");
        var bytes = File.ReadAllBytes(path);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(ManifestHash(id), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Hardware runtime manifest differs from the trusted catalog.");
        return JsonDocument.Parse(bytes);
    }

    private static string ContainedPath(string directory, string relative)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (Path.IsPathRooted(relative) || !path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Runtime file is outside the bundle.");
        Lopata.Updates.SafeUpdatePath.RejectLinks(path);
        return path;
    }

    public static bool HasCompleteLayout(string id, string directory)
    {
        try
        {
            using var manifest = ReadTrustedManifest(id, directory);
            return manifest.RootElement.GetProperty("files").EnumerateArray().All(file =>
            {
                var path = ContainedPath(directory, file.GetProperty("path").GetString()!);
                return File.Exists(path) && new FileInfo(path).Length == file.GetProperty("size").GetInt64();
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return false; }
    }

    public static async Task VerifyFilesAsync(string id, string directory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var manifest = ReadTrustedManifest(id, directory);
        RejectUnlistedFiles(directory, manifest, token);
        foreach (var file in manifest.RootElement.GetProperty("files").EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            var path = ContainedPath(directory, file.GetProperty("path").GetString()!);
            if (!File.Exists(path) || new FileInfo(path).Length != file.GetProperty("size").GetInt64())
                throw new InvalidDataException("Runtime file is missing or has an unexpected size: " + path);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
            if (!hash.Equals(file.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Runtime file SHA-256 differs from the trusted manifest: " + path);
        }
    }

    private static void RejectUnlistedFiles(string directory, JsonDocument manifest, CancellationToken token)
    {
        var expected = manifest.RootElement.GetProperty("files").EnumerateArray()
            .Select(file => ContainedPath(directory, file.GetProperty("path").GetString()!))
            .Append(Path.GetFullPath(Path.Combine(directory, "runtime-manifest.json")))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>(); pending.Enqueue(Path.GetFullPath(directory));
        var visited = 0;
        while (pending.TryDequeue(out var current))
        {
            token.ThrowIfCancellationRequested();
            Lopata.Updates.SafeUpdatePath.RejectLinks(current);
            foreach (var path in Directory.EnumerateFileSystemEntries(current))
            {
                token.ThrowIfCancellationRequested();
                if (++visited > 10000) throw new InvalidDataException("Unexpected runtime bundle size.");
                Lopata.Updates.SafeUpdatePath.RejectLinks(path);
                if (Directory.Exists(path)) pending.Enqueue(path);
                else if (!expected.Contains(Path.GetFullPath(path)))
                    throw new InvalidDataException("Runtime bundle contains an unlisted file: " + path);
            }
        }
    }

    public static async Task VerifyExecutableAsync(string id, string directory, CancellationToken token)
    {
        await VerifyFilesAsync(id, directory, token);
        var executable = ComponentCatalog.Find(id)?.HealthCheckRelativePath
            ?? throw new ArgumentException("Unknown hardware runtime.", nameof(id));
        var version = await RuntimeDeviceProbe.RunAsync(Path.Combine(directory, executable), "--version", token);
        var pinned = id is HardwareRuntimeCatalog.SdCpuId or HardwareRuntimeCatalog.SdVulkanId
            ? version.Contains("3f8527a", StringComparison.OrdinalIgnoreCase)
            : HardwareRuntimePreparation.IsPinnedVersion(version);
        if (!pinned)
            throw new InvalidDataException("Hardware runtime version does not match the pinned catalog.");
    }
}
