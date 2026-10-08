using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace AIHub.Services;

/// <summary>Verify every installed CPU file against a catalog-pinned, independently assembled profile.</summary>
internal static class PythonRuntimeBundleVerifier
{
    internal const string CpuManifestDigest = "97c5b8c7dc621f84431ee56aad5884344134d4a4d3ec7104a5cf00ffbba5919f";
    internal static string ManifestDigest(PythonRuntimeProfile profile) => profile switch
    {
        PythonRuntimeProfile.Cpu => CpuManifestDigest,
        PythonRuntimeProfile.Cuda126 => "6ea84073695e1912a46d1de2b5994635dfdb125b89ab614f9eb813285a2a4736",
        PythonRuntimeProfile.Cuda128 => "1225d9f2f8b3f6486dfd8bf6a6b1d4093c2eada162cb26c9ee5663b1e71832f5",
        PythonRuntimeProfile.Xpu => "3dca10b32d2f8863e57c3f5c066d643a80909ae1c1b69d426504fe44028c0a13",
        PythonRuntimeProfile.Rocm721 => "c33915a9687b2b49befad46304ccf31fad907e6cc0e736b1b986d018f2bd4c74",
        _ => throw new ArgumentOutOfRangeException(nameof(profile))
    };

    // A status hint only. Preparation and the installer verify every file before execution.
    internal static bool HasCpuLayout(string directory)
    {
        try
        {
            foreach (var relative in new[] { "python.exe", "python312.dll", "python312.zip", "python312._pth",
                "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll", "Notices/wheel-lock.json",
                "Lib/site-packages/torch/__init__.py", "Lib/site-packages/transformers/__init__.py" })
            {
                var path = Path.Combine(directory, relative);
                Lopata.Updates.SafeUpdatePath.RejectLinks(path);
                if (!File.Exists(path)) return false;
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { return false; }
    }

    internal static async Task VerifyCpuAsync(string directory, CancellationToken token) =>
        await VerifyAsync(PythonRuntimeProfile.Cpu, directory, token);

    internal static async Task VerifyAsync(PythonRuntimeProfile profile, string directory, CancellationToken token,
        Action<int, int>? progress = null)
    {
        token.ThrowIfCancellationRequested();
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "Tools", "python-hardware-" + profile.Flavor() + "-files.json");
        Lopata.Updates.SafeUpdatePath.RejectLinks(manifestPath);
        await using var manifestFile = File.OpenRead(manifestPath);
        if (manifestFile.Length > 8 * 1024 * 1024) throw new InvalidDataException("Oversized Python file manifest.");
        if (!Convert.ToHexString(await SHA256.HashDataAsync(manifestFile, token)).Equals(ManifestDigest(profile), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Python file manifest differs from the trusted catalog.");
        manifestFile.Position = 0;
        using var manifest = await JsonDocument.ParseAsync(manifestFile, cancellationToken: token);
        var files = new List<PinnedTreeFile>();
        foreach (var file in manifest.RootElement.GetProperty("files").EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            var relative = file.GetProperty("path").GetString()!;
            files.Add(new(relative, file.GetProperty("size").GetInt64(), file.GetProperty("sha256").GetString()!));
        }
        await PinnedFileTreeVerifier.VerifyAsync(directory, files, token, progress);
    }
}
