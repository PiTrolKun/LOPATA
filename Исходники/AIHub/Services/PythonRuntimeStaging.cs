using System.IO;

namespace AIHub.Services;

/// <summary>Assemble only a new stage. ComponentManager owns acquisition, licenses and the atomic replacement.</summary>
internal static class PythonRuntimeStaging
{
    internal static async Task PrepareCpuAsync(string stage, IReadOnlyDictionary<string, string> cache,
        string verifiedMsvcDirectory, CancellationToken token) =>
        await PrepareAsync(PythonRuntimeProfile.Cpu, stage, cache, verifiedMsvcDirectory, token);

    internal static async Task PrepareAsync(PythonRuntimeProfile profile, string stage, IReadOnlyDictionary<string, string> cache,
        string verifiedMsvcDirectory, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(stage) || !stage.EndsWith(".installing", StringComparison.Ordinal)
            || Directory.Exists(stage) || File.Exists(stage))
            throw new InvalidDataException("A fresh absolute Python installation stage is required.");
        await HardwareRuntimeBundleVerifier.VerifyFilesAsync(HardwareRuntimeCatalog.LlamaCpuId, verifiedMsvcDirectory, token);
        var artifacts = PythonBootstrapArtifacts.DownloadSet(profile);
        if (artifacts.Any(artifact => !cache.TryGetValue(artifact.FileName, out var path) || !File.Exists(path)))
            throw new FileNotFoundException("Pinned Python download set is incomplete.");
        Directory.CreateDirectory(stage);
        foreach (var artifact in artifacts)
            if (profile == PythonRuntimeProfile.Rocm721 && artifact.Name == "rocm")
                await PythonRocmSourceExtractor.ExtractAsync(artifact, cache[artifact.FileName], stage, token);
            else
                await PythonWheelExtractor.ExtractAsync(artifact, cache[artifact.FileName], stage, token,
                    wheelLayout: artifact != PythonBootstrapArtifacts.Python);
        await File.WriteAllTextAsync(Path.Combine(stage, "python312._pth"),
            "python312.zip\n.\nLib/site-packages\nimport site\n", token);
        foreach (var name in new[] { "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll" })
        {
            token.ThrowIfCancellationRequested();
            File.Copy(Path.Combine(verifiedMsvcDirectory, name), Path.Combine(stage, name), overwrite: true);
        }
        var notices = Path.Combine(stage, "Notices"); Directory.CreateDirectory(notices);
        foreach (var name in new[] { "music-MSVC-RUNTIME.txt", "python-intel-openmp-2025.3.1-EULA.txt",
            "python-intel-openmp-2025.3.1-NOTICES.txt" })
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Licenses", "texts", name), Path.Combine(notices, name));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Tools", profile.LockFile()),
            Path.Combine(notices, "wheel-lock.json"));
        if (profile == PythonRuntimeProfile.Rocm721)
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Licenses", "texts", "python-rocm721-NOTICES.txt"),
                Path.Combine(notices, "python-rocm721-NOTICES.txt"));
        // Probe/immutable receipt must succeed before this stage can be considered installed.
    }
}
