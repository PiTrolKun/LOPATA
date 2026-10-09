using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace AIHub.Services;

internal static class MusicDiffRhythmSource
{
    private static readonly SemaphoreSlim Gate = new(1);
    internal const string ArchiveDigest = "1A5ED6B66B125A7A69AC7867391FC619B30BE29FFA07C78D1FB8E884C1BCACF8";
    internal static string ArchivePath => Path.Combine(AppContext.BaseDirectory, "MusicDiffRhythmRuntime", "source.zip");
    internal static string DirectoryPath => Path.Combine(AppDataPaths.BaseDirectory, "Music", "DiffRhythmSource", MusicDiffRhythmCatalog.SourceRevision);
    internal static async Task VerifyAsync(CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try {
            Lopata.Updates.SafeUpdatePath.RejectLinks(ArchivePath);
            await using (var archive = File.OpenRead(ArchivePath))
                if (archive.Length != 25109972 || Convert.ToHexString(await SHA256.HashDataAsync(archive, token)) != ArchiveDigest)
                    throw new InvalidDataException("DiffRhythm source archive integrity failure.");
            var root = DirectoryPath;
            Lopata.Updates.SafeUpdatePath.RejectLinks(root);
            if (!Directory.Exists(root)) {
                var staging = root + "." + Guid.NewGuid().ToString("N") + ".partial";
                if (!Path.GetFullPath(staging).StartsWith(Path.GetFullPath(root) + ".", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("DiffRhythm source staging path escapes cache.");
                Directory.CreateDirectory(staging);
                try {
                    var artifact = new PinnedPythonArtifact("DiffRhythm source", MusicDiffRhythmCatalog.SourceRevision, "source.zip",
                        25109972, ArchiveDigest, new("https://huggingface.co/spaces/ASLP-lab/DiffRhythm2/tree/" + MusicDiffRhythmCatalog.SourceRevision));
                    await PythonWheelExtractor.ExtractAsync(artifact, ArchivePath, staging, token, wheelLayout: false);
                    await VerifyDirectoryAsync(staging, token);
                    token.ThrowIfCancellationRequested(); Directory.Move(staging, root);
                }
                finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
            }
            await VerifyDirectoryAsync(root, token);
        }
        finally { Gate.Release(); }
    }
    internal static async Task VerifyDirectoryAsync(string root, CancellationToken token)
    {
        Lopata.Updates.SafeUpdatePath.RejectLinks(root);
        var manifestPath = Path.Combine(root, "manifest.json");
        await using var input = File.OpenRead(manifestPath);
        if (Convert.ToHexString(await SHA256.HashDataAsync(input, token)) != "A56F6175270CFD6CDB4B20CF4C4978DB2D7AD770C3BB6F14CF9616D2727246C3")
            throw new InvalidDataException("DiffRhythm source manifest integrity failure.");
        input.Position = 0;
        var manifest = await JsonSerializer.DeserializeAsync<Manifest>(input, cancellationToken: token)
            ?? throw new InvalidDataException("Missing DiffRhythm source manifest.");
        if (manifest.Revision != MusicDiffRhythmCatalog.SourceRevision || manifest.Files.Count < 10)
            throw new InvalidDataException("Unsupported DiffRhythm engine revision.");
        var prefix = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var known = manifest.Files.Keys.Select(p => Path.GetFullPath(Path.Combine(root, p))).Append(manifestPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            if (!known.Contains(Path.GetFullPath(path))) throw new InvalidDataException("Unexpected file in DiffRhythm source bundle: " + path);
        foreach (var (relative, expected) in manifest.Files) {
            var path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("DiffRhythm source path escapes bundle.");
            Lopata.Updates.SafeUpdatePath.RejectLinks(path);
            await using var file = File.OpenRead(path);
            if (!Convert.ToHexString(await SHA256.HashDataAsync(file, token)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("DiffRhythm source integrity failure: " + relative);
        }
    }
    private sealed record Manifest(string Revision, Dictionary<string, string> Files);
}
