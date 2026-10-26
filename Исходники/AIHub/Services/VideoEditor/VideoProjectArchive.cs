using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace AIHub.Services.VideoEditor;

/// <summary>Portable media container. Internal operation/recovery snapshots remain small JSON files.</summary>
public static partial class VideoProjectArchive
{
    internal sealed record PackedFile(string File, long Length, string Sha256, DateTime LastWriteUtc);
    internal sealed record Manifest(int ArchiveSchema, VideoProject Project, PackedFile[] Files);
    internal const long ManifestLimit = 32L * 1024 * 1024;
    internal static void ProtectDestination(VideoProject p, string path)
    {
        path = Path.GetFullPath(path); Lopata.Updates.SafeUpdatePath.RejectLinks(path);
        if (p.Assets.Any(a => Path.GetFullPath(a.Path).Equals(path, StringComparison.OrdinalIgnoreCase)
            || a.Proxy is {} proxy && Path.GetFullPath(proxy).Equals(path, StringComparison.OrdinalIgnoreCase))
            || p.ImportedFiles.Any(f => Path.GetFullPath(f.Path).Equals(path, StringComparison.OrdinalIgnoreCase))) throw new IOException("VideoEditor.ProtectedSource");
        if (File.Exists(path) && DescribeId(path) != p.Id) throw new IOException("VideoEditor.ProtectedSource");
    }
    internal static string DescribeId(string path)
    {
        using var file = File.OpenRead(path);
        if (!IsZip(file)) return VideoProjectStore.Load(path).Id;
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        return ReadManifest(zip).Project.Id;
    }
    internal static bool IsZip(Stream stream)
    {
        var first = stream.ReadByte(); var second = stream.ReadByte(); stream.Position = 0; return first == 'P' && second == 'K';
    }
    internal static Manifest ReadManifest(ZipArchive zip)
    {
        var entry = zip.GetEntry("project.json") ?? throw new InvalidDataException("VideoEditor.InvalidArchive");
        if (entry.Length > ManifestLimit) throw new InvalidDataException("VideoEditor.InvalidArchive");
        using var input = entry.Open(); using var memory = new MemoryStream(); Copy(input, memory, entry.Length, CancellationToken.None);
        return JsonSerializer.Deserialize<Manifest>(memory.ToArray()) ?? throw new InvalidDataException("VideoEditor.InvalidArchive");
    }
    internal static string Copy(Stream source, Stream destination, long expected, CancellationToken token, Action<long>? progress = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var bytes = new byte[128 * 1024]; long total = 0; int count;
        while ((count = source.Read(bytes)) != 0) {
            token.ThrowIfCancellationRequested(); total = checked(total + count);
            if (total > expected) throw new InvalidDataException("VideoEditor.InvalidArchive");
            destination.Write(bytes, 0, count); hash.AppendData(bytes, 0, count); progress?.Invoke(count);
        }
        token.ThrowIfCancellationRequested(); if (total != expected) throw new InvalidDataException("VideoEditor.InvalidArchive");
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    public static void Save(VideoProject project, string destination, CancellationToken token = default, IProgress<double>? progress = null)
    {
        project.Validate(); VideoProjectStore.VerifySources(project); ProtectDestination(project, destination); token.ThrowIfCancellationRequested();
        var p = project.Clone(); VideoSeries.Ensure(p); p.Provenance = VideoProjectStore.Provenance(p);
        _ = p.Provenance.Json();
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); var files = new List<PackedFile>();
        string Add(string source, string? entry = null) {
            source = Path.GetFullPath(source); Lopata.Updates.SafeUpdatePath.RejectLinks(source);
            if (sources.TryGetValue(source, out var existing)) return existing;
            var extension = Path.GetExtension(source).ToLowerInvariant();
            if (extension.Length > 16 || extension.Skip(1).Any(c => !char.IsAsciiLetterOrDigit(c))) extension = ".bin";
            entry ??= "media/" + sources.Count.ToString("D5", System.Globalization.CultureInfo.InvariantCulture) + extension;
            sources.Add(source, entry); return entry;
        }
        foreach (var asset in p.Assets) {
            var original = project.Asset(asset.Id); asset.Path = Add(original.Path);
            asset.Proxy = original.Proxy is {} proxy && File.Exists(proxy) ? Add(proxy) : null;
        }
        for (var i = 0; i < p.ImportedFiles.Count; i++) {
            var original = project.ImportedFiles[i]; var info = new FileInfo(original.Path);
            if (!info.Exists || info.Length != original.Length || info.LastWriteTimeUtc != original.LastWriteUtc) throw new IOException("VideoEditor.SourceChanged");
            p.ImportedFiles[i] = original with { Path = Add(original.Path) };
        }
        // Only known media caches, never operation logs, model files or arbitrary directory contents.
        var preview = VideoPreviewAssets.DirectoryFor(project);
        foreach (var asset in project.Assets) foreach (var suffix in new[] { ".png", ".wav", "-gop1.mp4", ".audio.m4a", ".wave-peaks-v2.bin" }) {
            var source = Path.Combine(preview, asset.Id + suffix);
            if (File.Exists(source)) Add(source, "Preview/" + asset.Id + suffix);
        }
        // A proxy first added above may also be a known cache. Preserve that mapping in the manifest.
        var path = Path.GetFullPath(destination); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var total = sources.Keys.Sum(s => new FileInfo(s).Length); long copied = 0; var clock = System.Diagnostics.Stopwatch.StartNew(); long reported = 0;
        void Report(long n) { copied += n; if (clock.ElapsedMilliseconds - reported < 100) return; reported = clock.ElapsedMilliseconds; progress?.Report(total == 0 ? 1 : copied / (double)total); }
        try {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)) {
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true)) {
                    foreach (var pair in sources) {
                        token.ThrowIfCancellationRequested(); var before = new FileInfo(pair.Key); var length = before.Length; var modified = before.LastWriteTimeUtc;
                        using var input = new FileStream(pair.Key, FileMode.Open, FileAccess.Read, FileShare.Read);
                        using var target = zip.CreateEntry(pair.Value, CompressionLevel.NoCompression).Open();
                        var sha = Copy(input, target, length, token, Report);
                        before.Refresh(); if (before.Length != length || before.LastWriteTimeUtc != modified) throw new IOException("VideoEditor.SourceChanged");
                        files.Add(new(pair.Value, length, sha, modified));
                    }
                    var data = JsonSerializer.SerializeToUtf8Bytes(new Manifest(1, p, files.ToArray()), new JsonSerializerOptions { WriteIndented = true });
                    if (data.Length > ManifestLimit) throw new InvalidDataException("VideoEditor.InvalidArchive");
                    using var manifest = zip.CreateEntry("project.json", CompressionLevel.Optimal).Open(); manifest.Write(data);
                }
                output.Flush(true);
            }
            token.ThrowIfCancellationRequested(); VideoProjectStore.VerifySources(project); File.Move(temporary, path, true); progress?.Report(1);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
