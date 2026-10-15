using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>A pinned, separately licensed dataset in the shared runtime; never a user's work library.</summary>
public static class MusicRhymeCorpus
{
    public const string ComponentId = "data.rhyme-ru-en";
    public static string DirectoryPath => Path.Combine(AppDataPaths.RuntimeDirectory, "RAG", "rhyme-ru-en");
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? _verifiedStamp;
    internal static JsonElement Resource(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var key = assembly.GetManifestResourceNames().Single(n => n.EndsWith(name, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(key)!;
        using var document = JsonDocument.Parse(stream); return document.RootElement.Clone();
    }
    public static async Task<string> EnsureAsync(IProgress<RagBookProgress> progress, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try
        {
            await ComponentLicenseGate.EnsureAsync(ComponentId, token);
            var root = DirectoryPath;
            if (!File.Exists(Path.Combine(root, "corpus.json"))) await DownloadAsync(progress, token);
            var manifest = Resource("MusicRhymeCorpus.json");
            var stamp = string.Join(";", manifest.GetProperty("files").EnumerateArray().Select(f => {
                var info = new FileInfo(Contained(root, f.GetProperty("path").GetString()!));
                return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "missing";
            }));
            var receipt = new FileInfo(Path.Combine(root, "corpus.json"));
            stamp += $";{receipt.Length}:{receipt.LastWriteTimeUtc.Ticks}";
            if (_verifiedStamp == stamp) return root;
            progress.Report(new("Verifying"));
            await VerifyAsync(root, manifest, token); _verifiedStamp = stamp; return root;
        }
        finally { Gate.Release(); }
    }
    internal static string Contained(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains(':') || relative.Split('/', '\\').Any(p => p is ".." or "."))
            throw new InvalidDataException("Music.Rhyme.CorpusInvalid");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Music.Rhyme.CorpusInvalid");
        Lopata.Updates.SafeUpdatePath.RejectLinks(path); return path;
    }
    private static async Task VerifyAsync(string root, JsonElement manifest, CancellationToken token)
    {
        var delivery = Resource("MusicRhymeDelivery.json");
        if (await HashAsync(Path.Combine(root, "corpus.json"), token) != delivery.GetProperty("corpus_sha256").GetString())
            throw new InvalidDataException("Music.Rhyme.CorpusInvalid");
        foreach (var file in manifest.GetProperty("files").EnumerateArray())
        {
            token.ThrowIfCancellationRequested(); var path = Contained(root, file.GetProperty("path").GetString()!);
            if (!File.Exists(path) || new FileInfo(path).Length != file.GetProperty("bytes").GetInt64()
                || await HashAsync(path, token) != file.GetProperty("sha256").GetString())
                throw new InvalidDataException("Music.Rhyme.CorpusInvalid");
        }
    }
    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        using var stream = File.OpenRead(path); return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
    }
    private static async Task DownloadAsync(IProgress<RagBookProgress> progress, CancellationToken token)
    {
        await ComponentLicenseGate.EnsureAsync(ComponentId, token);
        var delivery = Resource("MusicRhymeDelivery.json");
        var parent = Path.GetDirectoryName(DirectoryPath)!; Directory.CreateDirectory(parent);
        var target = Path.Combine(parent, delivery.GetProperty("filename").GetString()!);
        var file = new ManagedModelArtifactFile { RelativePath = Path.GetFileName(target),
            SourceUrl = delivery.GetProperty("url").GetString()!, SizeBytes = delivery.GetProperty("bytes").GetInt64(),
            Sha256 = delivery.GetProperty("sha256").GetString()! };
        using var http = new HttpClient();
        var loader = new SegmentedModelFileDownloader(http) { MaximumParallelConnections = new AppSettingsStore().LoadOrCreate().ModelDownloads?.MaximumParallelConnections ?? 0 };
        if (!File.Exists(target) || await HashAsync(target, token) != file.Sha256)
        {
            progress.Report(new("Downloading"));
            await loader.DownloadAsync(new() { ModelArtifactId = ComponentId, DisplayName = "LOPATA Rhyme RU/EN" }, file, target, 0,
                new InlineProgress<ManagedModelDownloadProgress>(_ => progress.Report(new("Downloading"))), token);
            if (await HashAsync(target + ".part", token) != file.Sha256) throw new InvalidDataException("Music.Rhyme.CorpusInvalid");
            File.Move(target + ".part", target, true);
        }
        var staging = Path.Combine(parent, ".rhyme-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var manifest = Resource("MusicRhymeCorpus.json");
            var allowed = manifest.GetProperty("files").EnumerateArray().ToDictionary(f => f.GetProperty("path").GetString()!, f => f.GetProperty("bytes").GetInt64());
            allowed.Add("corpus.json", delivery.GetProperty("unpacked_bytes").GetInt64() - allowed.Values.Sum());
            using (var archive = ZipFile.OpenRead(target))
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in archive.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    const string prefix = "rhyme-ru-en/";
                    if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("Music.Rhyme.CorpusInvalid");
                    var relative = entry.FullName[prefix.Length..];
                    if (!allowed.TryGetValue(relative, out var length) || !names.Add(relative) || entry.Length != length)
                        throw new InvalidDataException("Music.Rhyme.CorpusInvalid");
                    var path = Contained(staging, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    using var source = entry.Open(); using var output = File.Create(path); await source.CopyToAsync(output, token);
                }
                if (names.Count != allowed.Count) throw new InvalidDataException("Music.Rhyme.CorpusInvalid");
            }
            await VerifyAsync(staging, manifest, token);
            if (Directory.Exists(DirectoryPath)) throw new IOException("Music.Rhyme.CorpusInvalid");
            Directory.Move(staging, DirectoryPath);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
}
