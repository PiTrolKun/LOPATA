using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace AIHub.Services;

internal static class MusicHeartMuLaSource
{
    private static readonly SemaphoreSlim Gate = new(1);
    internal const string ArchiveDigest = "83EB311C33655A22574073A6E6E6704151363D461770DB0566B2996A5BB2C892";
    internal static string ArchivePath => Path.Combine(AppContext.BaseDirectory, "MusicHeartMuLaRuntime", "source.zip");
    internal static string DirectoryPath => Path.Combine(AppDataPaths.BaseDirectory, "Music", "HeartMuLaSource", MusicHeartMuLaCatalog.SourceRevision);
    internal static async Task VerifyAsync(CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try {
            Lopata.Updates.SafeUpdatePath.RejectLinks(ArchivePath);
            await using (var archive = File.OpenRead(ArchivePath))
                if (archive.Length != 50806 || Convert.ToHexString(await SHA256.HashDataAsync(archive, token)) != ArchiveDigest)
                    throw new InvalidDataException("HeartMuLa source archive integrity failure.");
            var root = DirectoryPath;
            Lopata.Updates.SafeUpdatePath.RejectLinks(root);
            if (!Directory.Exists(root)) {
                var staging = root + "." + Guid.NewGuid().ToString("N") + ".partial";
                if (!Path.GetFullPath(staging).StartsWith(Path.GetFullPath(root) + ".", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("HeartMuLa source staging path escapes cache.");
                Directory.CreateDirectory(staging);
                try {
                    var artifact = new PinnedPythonArtifact("HeartMuLa source", MusicHeartMuLaCatalog.SourceRevision, "source.zip",
                        50806, ArchiveDigest, new("https://github.com/HeartMuLa/heartlib/tree/" + MusicHeartMuLaCatalog.SourceRevision));
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
        if (Convert.ToHexString(await SHA256.HashDataAsync(input, token)) != "268D84CF17A7500091C82A4F223DA8F943435627636811587B6D99BEFF20FC77")
            throw new InvalidDataException("HeartMuLa source manifest integrity failure.");
        input.Position = 0;
        var manifest = await JsonSerializer.DeserializeAsync<Manifest>(input, cancellationToken: token)
            ?? throw new InvalidDataException("Missing HeartMuLa source manifest.");
        if (manifest.Revision != MusicHeartMuLaCatalog.SourceRevision || manifest.Files.Count < 10)
            throw new InvalidDataException("Unsupported HeartMuLa engine revision.");
        var prefix = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var known = manifest.Files.Keys.Select(p => Path.GetFullPath(Path.Combine(root, p))).Append(manifestPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            if (!known.Contains(Path.GetFullPath(path))) throw new InvalidDataException("Unexpected file in HeartMuLa source bundle: " + path);
        foreach (var (relative, expected) in manifest.Files) {
            var path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("HeartMuLa source path escapes bundle.");
            Lopata.Updates.SafeUpdatePath.RejectLinks(path);
            await using var file = File.OpenRead(path);
            if (!Convert.ToHexString(await SHA256.HashDataAsync(file, token)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("HeartMuLa source integrity failure: " + relative);
        }
    }
    private sealed record Manifest(string Revision, Dictionary<string, string> Files);
}
