using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Isolated, pinned Python overlay. Torch comes only from the standard hardware downloader.</summary>
internal static class MusicBf16Runtime
{
    private static readonly SemaphoreSlim Gate = new(1);
    internal static async Task<string> PrepareAsync(string modelsRoot, CancellationToken token)
    {
        await ComponentLicenseGate.EnsureAsync(MusicModelVariants.Components(MusicModelVariants.Bf16), token);
        var cards = MusicModelVariants.Cards(modelsRoot, MusicModelVariants.Bf16);
        var store = new ManagedModelLibraryStore();
        using var downloads = new ManagedModelAcquisitionService(store);
        foreach (var card in cards) {
            store.Upsert(card);
            var checkedCard = await downloads.VerifyAsync(card.ModelArtifactId, null, token);
            if (checkedCard.Status != ManagedModelStatuses.Installed) throw new IOException("Prepare YuE2 BF16 files through LOPATA's model downloader first.");
        }
        var runtime = cards.Single(c => c.ModelArtifactId == MusicModelVariants.RuntimeId);
        var root = Path.Combine(AppDataPaths.BaseDirectory, "Music", "Python", MusicModelVariants.RuntimeRevision);
        await Gate.WaitAsync(token);
        try {
            if (Directory.Exists(root)) { await VerifyAsync(root, runtime, token); return Path.Combine(root, "Lib", "site-packages"); }
            var staging = root + "." + Guid.NewGuid().ToString("N") + ".partial";
            Directory.CreateDirectory(staging);
            try {
                foreach (var file in runtime.Files) {
                    var artifact = new PinnedPythonArtifact("YuE2", "pinned", file.RelativePath, file.SizeBytes, file.Sha256, new(file.SourceUrl));
                    await PythonWheelExtractor.ExtractAsync(artifact, Path.Combine(runtime.InstallDirectory, file.RelativePath), staging, token);
                }
                var manifest = new Dictionary<string, string>();
                foreach (var path in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
                    manifest[Path.GetRelativePath(staging, path)] = await Hash(path, token);
                await File.WriteAllTextAsync(Path.Combine(staging, "files.json"), JsonSerializer.Serialize(manifest), token);
                token.ThrowIfCancellationRequested(); Directory.Move(staging, root);
            }
            finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
            return Path.Combine(root, "Lib", "site-packages");
        }
        finally { Gate.Release(); }
    }
    private static async Task VerifyAsync(string root, ManagedModelArtifactCard runtime, CancellationToken token)
    {
        Lopata.Updates.SafeUpdatePath.RejectLinks(root);
        var prefix = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var expectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.Combine(root, "files.json") };
        // Anchor verification in the pinned upstream wheels, never a mutable local receipt.
        foreach (var wheel in runtime.Files) {
            var path = Path.Combine(runtime.InstallDirectory, wheel.RelativePath);
            await using var file = File.OpenRead(path);
            if (file.Length != wheel.SizeBytes || !Convert.ToHexString(await SHA256.HashDataAsync(file, token)).Equals(wheel.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("YuE2 wheel integrity failure.");
            file.Position = 0; using var archive = new ZipArchive(file, ZipArchiveMode.Read, true);
            foreach (var entry in archive.Entries.Where(e => !e.FullName.EndsWith('/'))) {
                var relative = PythonWheelExtractor.RelativeDestination(entry.FullName);
                var full = Path.GetFullPath(Path.Combine(root, relative));
                if (!expectedPaths.Add(full)) throw new InvalidDataException("Conflicting Python overlay files.");
                if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Python overlay path escapes installation.");
                Lopata.Updates.SafeUpdatePath.RejectLinks(full);
                await using var source = entry.Open();
                var expected = Convert.ToHexString(await SHA256.HashDataAsync(source, token));
                if (new FileInfo(full).Length != entry.Length || await Hash(full, token) != expected) throw new InvalidDataException("YuE2 Python library integrity failure.");
            }
        }
        foreach (var installed in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            if (!expectedPaths.Contains(installed)) throw new InvalidDataException("Unexpected Python overlay file: " + Path.GetFileName(installed));
    }
    private static async Task<string> Hash(string path, CancellationToken token)
    { await using var stream = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(stream, token)); }
}
