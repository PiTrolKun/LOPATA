using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace AIHub.Services;

/// <summary>Native sub-bundle integrity in addition to the enclosing engine-set manifest.</summary>
internal static class NativeEngineBundleVerifier
{
    private static JsonDocument Read(string family, string backend, string directory, string engineSetId)
    {
        var path = Path.Combine(directory, "runtime-manifest.json");
        Lopata.Updates.SafeUpdatePath.RejectLinks(path);
        if (!File.Exists(path) || new FileInfo(path).Length > 512 * 1024)
            throw new InvalidDataException("Native runtime manifest is missing or too large.");
        var bytes = File.ReadAllBytes(path);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(NativeRuntimeReleasePolicy.ManifestDigest(family, backend, engineSetId), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Native runtime manifest is not the pinned engine-set payload.");
        var manifest = JsonDocument.Parse(bytes);
        if (manifest.RootElement.GetProperty("backend").GetString() != backend
            || manifest.RootElement.GetProperty("revision").GetString() != (family == "llama"
                ? NativeRuntimeReleasePolicy.LlamaRevision(engineSetId) : NativeRuntimeReleasePolicy.SdRevision(engineSetId)))
        { manifest.Dispose(); throw new InvalidDataException("Native runtime identity differs from the selected engine set."); }
        return manifest;
    }

    private static string ContainedPath(string directory, string relative)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (Path.IsPathRooted(relative) || !path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Native runtime file escapes its bundle.");
        Lopata.Updates.SafeUpdatePath.RejectLinks(path);
        return path;
    }

    internal static bool HasCompleteLayout(string family, string backend, string directory, string engineSetId = NativeRuntimeReleasePolicy.UpdatedSet)
    {
        try
        {
            using var manifest = Read(family, backend, directory, engineSetId);
            return manifest.RootElement.GetProperty("files").EnumerateArray().All(item =>
            {
                var path = ContainedPath(directory, item.GetProperty("path").GetString()!);
                return File.Exists(path) && new FileInfo(path).Length == item.GetProperty("size").GetInt64();
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return false; }
    }

    internal static async Task VerifyFilesAsync(string family, string backend, string directory, CancellationToken token,
        string engineSetId = NativeRuntimeReleasePolicy.UpdatedSet)
    {
        using var manifest = Read(family, backend, directory, engineSetId);
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { Path.GetFullPath(Path.Combine(directory, "runtime-manifest.json")) };
        foreach (var item in manifest.RootElement.GetProperty("files").EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            var path = ContainedPath(directory, item.GetProperty("path").GetString()!);
            if (!expected.Add(path) || !File.Exists(path) || new FileInfo(path).Length != item.GetProperty("size").GetInt64())
                throw new InvalidDataException("Missing, duplicate or altered native runtime file.");
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (!Convert.ToHexString(await SHA256.HashDataAsync(file, token)).Equals(item.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Native runtime file checksum differs from the trusted payload.");
        }
        var pending = new Queue<string>(); pending.Enqueue(directory); var count = 0;
        while (pending.TryDequeue(out var current))
        {
            token.ThrowIfCancellationRequested();
            Lopata.Updates.SafeUpdatePath.RejectLinks(current);
            foreach (var path in Directory.EnumerateFileSystemEntries(current))
            {
                if (++count > 10000) throw new InvalidDataException("Unexpected native runtime bundle size.");
                Lopata.Updates.SafeUpdatePath.RejectLinks(path);
                if (Directory.Exists(path)) pending.Enqueue(path);
                else if (!expected.Contains(Path.GetFullPath(path))) throw new InvalidDataException("Unlisted native runtime file.");
            }
        }
    }
}
