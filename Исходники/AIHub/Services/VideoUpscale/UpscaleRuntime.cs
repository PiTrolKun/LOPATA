using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using AIHub.Models;
using AIHub.Services.VideoEditor;

namespace AIHub.Services.VideoUpscale;

/// <summary>Expand original verified archives. No pip, setup scripts, or system Python changes.</summary>
internal static class UpscaleRuntime
{
    private static readonly SemaphoreSlim Gate = new(1);
    internal static string Expanded(ManagedModelArtifactCard card) => Path.Combine(card.InstallDirectory, "Expanded");
    internal static async Task<string> PrepareAsync(ManagedModelArtifactCard card, CancellationToken token, bool reinstall = false)
    {
        await Gate.WaitAsync(token);
        try {
            var root = Expanded(card); var identity = RuntimeFileAudit.Revision(card.Revision, card.Files.Select(f => new PinnedTreeFile(f.RelativePath, f.SizeBytes, f.Sha256)).ToArray());
            if (!Directory.Exists(root) || reinstall) {
                var staging = root + "." + Guid.NewGuid().ToString("N") + ".partial";
                Lopata.Updates.SafeUpdatePath.RejectLinks(staging); Directory.CreateDirectory(staging);
                try {
                    var paths = await ProcessAsync(card, staging, true, token);
                    var previous = root + ".previous-" + Guid.NewGuid().ToString("N");
                    Lopata.Updates.SafeUpdatePath.RejectLinks(root); Lopata.Updates.SafeUpdatePath.RejectLinks(previous);
                    if (Directory.Exists(root)) Directory.Move(root, previous);
                    try { Directory.Move(staging, root); ComponentAudit.Invalidate(root); }
                    catch { if (Directory.Exists(previous)) Directory.Move(previous, root); throw; }
                } finally {
                    // Only this newly generated staging directory, never an installed/user tree.
                    var resolved = Path.GetFullPath(staging);
                    if (!resolved.StartsWith(Path.GetFullPath(card.InstallDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsafe runtime staging directory.");
                    Lopata.Updates.SafeUpdatePath.RejectLinks(resolved);
                    if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
                }
            }
            await ComponentAudit.RunAsync(card.ModelArtifactId + ".expanded", root, identity,
                ct => RuntimeFileAudit.PresenceInventoryAsync(root, identity, ct), async ct => {
                    var expected = await ProcessAsync(card, root, false, ct);
                    foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) {
                        var relative = Path.GetRelativePath(root, f).Replace('\\', '/');
                        // Upstream CLI creates its own logs/outputs; those are data, never audited runtime payloads.
                        if (card.ModelArtifactId == "video-upscale-visual-enhancer" && (relative.StartsWith("logs/", StringComparison.Ordinal) || relative.StartsWith("outputs/", StringComparison.Ordinal))) continue;
                        if (!expected.Contains(Path.GetFullPath(f))) throw new InvalidDataException("Unexpected upscale runtime file: " + relative);
                    }
                    await RuntimeFileAudit.SaveInventoryAsync(root, identity, expected.Select(f => Path.GetRelativePath(root, f)), ct);
                }, token);
            return root;
        } finally { Gate.Release(); }
    }
    private static async Task<HashSet<string>> ProcessAsync(ManagedModelArtifactCard card, string root, bool extract, CancellationToken token)
    {
        HashSet<string> expected = new(StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in card.Files) {
            var source = RuntimeFileAudit.Contained(card.InstallDirectory, artifact.RelativePath);
            Lopata.Updates.SafeUpdatePath.RejectLinks(source);
            await using var file = File.OpenRead(source);
            if (file.Length != artifact.SizeBytes || !Convert.ToHexString(await SHA256.HashDataAsync(file, token)).Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Upscale archive integrity failure.");
            file.Position = 0;
            if (artifact.RelativePath.EndsWith(".tar.gz", StringComparison.Ordinal)) {
                using var gzip = new GZipStream(file, CompressionMode.Decompress); using var tar = new TarReader(gzip);
                long bytes = 0; int count = 0;
                while (await tar.GetNextEntryAsync(cancellationToken: token) is {} entry) {
                    if (++count > 50000 || (bytes += entry.Length) > 1024L * 1024 * 1024) throw new InvalidDataException("Oversized Python archive.");
                    if (entry.EntryType == TarEntryType.Directory) continue;
                    if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || !entry.Name.StartsWith("python/", StringComparison.Ordinal)) throw new InvalidDataException("Unsafe Python archive entry.");
                    await Entry(entry.Name[7..], entry.Length, entry.DataStream ?? Stream.Null);
                }
            } else {
                using var zip = new ZipArchive(file, ZipArchiveMode.Read, true);
                if (zip.Entries.Count > 100000 || zip.Entries.Sum(e => e.Length) > 12L * 1024 * 1024 * 1024) throw new InvalidDataException("Oversized upscale archive.");
                string? prefix = null;
                foreach (var entry in zip.Entries.Where(e => !e.FullName.EndsWith('/'))) {
                    if ((entry.ExternalAttributes >> 16 & 0xF000) == 0xA000) throw new InvalidDataException("Archive links are forbidden.");
                    var relative = entry.FullName;
                    if (artifact.Purpose == "source") {
                        var slash = relative.IndexOf('/'); if (slash < 1) throw new InvalidDataException("Missing source root.");
                        prefix ??= relative[..slash]; if (prefix != relative[..slash]) throw new InvalidDataException("Ambiguous source archive.");
                        relative = relative[(slash + 1)..];
                    } else if (artifact.Purpose == "python-wheel") relative = PythonWheelExtractor.RelativeDestination(relative);
                    await using var payload = entry.Open(); await Entry(relative, entry.Length, payload);
                }
            }
        }
        return expected;
        async Task Entry(string relative, long length, Stream payload) {
            token.ThrowIfCancellationRequested();
            var target = RuntimeFileAudit.Contained(root, relative); Lopata.Updates.SafeUpdatePath.RejectLinks(target);
            if (!expected.Add(target)) throw new InvalidDataException("Conflicting upscale runtime entries: " + relative);
            if (extract) {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write); await payload.CopyToAsync(output, token);
            } else {
                await using var actual = File.OpenRead(target);
                var expectedHash = await SHA256.HashDataAsync(payload, token); var actualHash = await SHA256.HashDataAsync(actual, token);
                if (actual.Length != length || !CryptographicOperations.FixedTimeEquals(expectedHash, actualHash)) throw new InvalidDataException("Upscale runtime differs from original archive: " + relative);
            }
        }
    }
}
