using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Isolated, pinned Python overlay. Torch comes only from the standard hardware downloader.</summary>
internal static class MusicDiffRhythmRuntime
{
    private static readonly SemaphoreSlim Gate = new(1);
    internal sealed record Prepared(string Overlay, IReadOnlyList<ManagedModelArtifactCard> Cards);
    internal static async Task<Prepared> PrepareAsync(string modelsRoot, CancellationToken token, Action<string>? log = null)
    {
        await ComponentLicenseGate.EnsureAsync(MusicDiffRhythmCatalog.Components, token);
        var cards = MusicDiffRhythmCatalog.Cards(modelsRoot);
        var store = new ManagedModelLibraryStore();
        using var downloads = new ManagedModelAcquisitionService(store);
        var verified = new List<ManagedModelArtifactCard>();
        foreach (var card in cards) {
            store.Upsert(card);
            log?.Invoke("[Prepare] Verifying " + card.DisplayName);
            var checkedCard = await downloads.VerifyAsync(card.ModelArtifactId, new InlineProgress(p =>
                log?.Invoke($"[Prepare] {p.FileName} · {p.DownloadedBytes / 1e9:0.00}/{p.TotalBytes / 1e9:0.00} GB")), token);
            if (checkedCard.Status != ManagedModelStatuses.Installed) throw new IOException("Prepare DiffRhythm 2 files through LOPATA's model downloader first.");
            verified.Add(checkedCard);
        }
        var runtime = cards.Single(c => c.ModelArtifactId == MusicDiffRhythmCatalog.RuntimeId);
        var root = Path.Combine(AppDataPaths.BaseDirectory, "Music", "Python", MusicDiffRhythmCatalog.RuntimeRevision);
        await Gate.WaitAsync(token);
        try {
            if (Directory.Exists(root)) {
                log?.Invoke("[Prepare] Verifying installed DiffRhythm Python libraries");
                await VerifyAsync(root, runtime, token, log); return new(Path.Combine(root, "Lib", "site-packages"), verified);
            }
            var staging = root + "." + Guid.NewGuid().ToString("N") + ".partial";
            Directory.CreateDirectory(staging);
            try {
                foreach (var file in runtime.Files) {
                    log?.Invoke("[Prepare] Extracting " + file.RelativePath);
                    var artifact = new PinnedPythonArtifact("DiffRhythm", "pinned", file.RelativePath, file.SizeBytes, file.Sha256, new(file.SourceUrl));
                    await PythonWheelExtractor.ExtractAsync(artifact, Path.Combine(runtime.InstallDirectory, file.RelativePath), staging, token);
                }
                var manifest = new Dictionary<string, string>();
                foreach (var path in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
                    manifest[Path.GetRelativePath(staging, path)] = await Hash(path, token);
                await File.WriteAllTextAsync(Path.Combine(staging, "files.json"), JsonSerializer.Serialize(manifest), token);
                await VerifyAsync(staging, runtime, token, log);
                token.ThrowIfCancellationRequested(); Directory.Move(staging, root);
            }
            finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
            return new(Path.Combine(root, "Lib", "site-packages"), verified);
        }
        finally { Gate.Release(); }
    }
    private static async Task VerifyAsync(string root, ManagedModelArtifactCard runtime, CancellationToken token, Action<string>? log)
    {
        // Embedded in the trusted application, generated from SHA256-verified wheels.
        // Mutable files.json is only an installation receipt, never the source of expected hashes.
        await using var input = typeof(MusicDiffRhythmRuntime).Assembly.GetManifestResourceStream("AIHub.DiffRhythmPythonManifest")
            ?? throw new InvalidDataException("Missing pinned DiffRhythm overlay manifest.");
        using var manifest = await JsonDocument.ParseAsync(input, cancellationToken: token);
        var wheels = manifest.RootElement.GetProperty("wheels").EnumerateArray().ToDictionary(
            row => row.GetProperty("path").GetString()!, row => row.GetProperty("sha256").GetString()!);
        if (wheels.Count != runtime.Files.Count || runtime.Files.Any(file =>
            !wheels.TryGetValue(file.RelativePath, out var sha) || !sha.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("DiffRhythm overlay manifest belongs to another wheel revision.");
        var files = manifest.RootElement.GetProperty("files").EnumerateArray().Select(row => new PinnedTreeFile(
            row.GetProperty("path").GetString()!, row.GetProperty("size").GetInt64(), row.GetProperty("sha256").GetString()!)).ToArray();
        var reported = 0;
        await PinnedFileTreeVerifier.VerifyAsync(root, files, token, (done, total) => {
            var bucket = done * 20 / total;
            if (bucket > Volatile.Read(ref reported) && Interlocked.Exchange(ref reported, bucket) < bucket)
                log?.Invoke($"[Prepare] DiffRhythm libraries · {done}/{total}");
        }, ignoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "files.json" });
    }
    private static async Task<string> Hash(string path, CancellationToken token)
    { await using var stream = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(stream, token)); }
    private sealed class InlineProgress(Action<ManagedModelDownloadProgress> report) : IProgress<ManagedModelDownloadProgress>
    { public void Report(ManagedModelDownloadProgress value) => report(value); }
}
