using System.IO;
using System.IO.Compression;

namespace AIHub.Services.VideoEditor;

public static partial class VideoProjectArchive
{
    public static VideoProject Load(string source, CancellationToken token = default, IProgress<double>? progress = null)
    {
        try { return LoadCore(source, token, progress); }
        catch (Exception error) when (error is System.Text.Json.JsonException || error is InvalidDataException && !error.Message.StartsWith("VideoEditor.", StringComparison.Ordinal)) { throw new InvalidDataException("VideoEditor.InvalidArchive", error); }
    }
    private static VideoProject LoadCore(string source, CancellationToken token, IProgress<double>? progress)
    {
        source = Path.GetFullPath(source); Lopata.Updates.SafeUpdatePath.RejectLinks(source);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var zip = new ZipArchive(input, ZipArchiveMode.Read);
        var manifest = ReadManifest(zip); var p = manifest.Project;
        if (manifest.ArchiveSchema != 1 || p is null || !Guid.TryParseExact(p.Id, "N", out _) || manifest.Files is null || manifest.Files.Length > 100000) throw new InvalidDataException("VideoEditor.InvalidArchive");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries) if (!names.Add(entry.FullName)) throw new InvalidDataException("VideoEditor.InvalidArchive");
        if (zip.Entries.Count != manifest.Files.Length + 1) throw new InvalidDataException("VideoEditor.InvalidArchive");
        var members = new Dictionary<string, PackedFile>(StringComparer.Ordinal); long total = 0;
        foreach (var file in manifest.Files) {
            if (file is null || !SafeMember(file.File) || !members.TryAdd(file.File, file) || file.Length < 0 || file.Sha256 is null || file.Sha256.Length != 64 || file.Sha256.Any(c => !char.IsAsciiHexDigit(c))
                || zip.GetEntry(file.File) is not {} entry || entry.Length != file.Length) throw new InvalidDataException("VideoEditor.InvalidArchive");
            total = checked(total + file.Length);
        }
        if (p.Assets is null || p.Assets.Any(a => a is null || !members.ContainsKey(a.Path) || a.Proxy is {} proxy && !members.ContainsKey(proxy))) throw new InvalidDataException("VideoEditor.InvalidArchive");
        if (p.ImportedFiles is null || p.ImportedFiles.Any(f => f is null || !members.ContainsKey(f.Path))) throw new InvalidDataException("VideoEditor.InvalidArchive");
        token.ThrowIfCancellationRequested();
        var parent = Path.Combine(VideoProjectStore.Work(p.Id), "Packages"); Lopata.Updates.SafeUpdatePath.RejectLinks(parent); Directory.CreateDirectory(parent);
        // Unique extraction per open prevents archive aliases from overwriting another open project's media.
        var directory = Path.Combine(parent, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        long copied = 0; var clock = System.Diagnostics.Stopwatch.StartNew(); long reported = 0;
        void Report(long n) { copied += n; if (clock.ElapsedMilliseconds - reported < 100) return; reported = clock.ElapsedMilliseconds; progress?.Report(total == 0 ? 1 : copied / (double)total); }
        try {
            var drive = new DriveInfo(Path.GetPathRoot(directory)!); if (drive.IsReady && drive.AvailableFreeSpace < checked(total + ManifestLimit)) throw new IOException("VideoEditor.ArchiveDiskSpace");
            foreach (var file in manifest.Files) {
                token.ThrowIfCancellationRequested(); var path = Path.Combine(directory, file.File.Replace('/', Path.DirectorySeparatorChar));
                Lopata.Updates.SafeUpdatePath.RejectLinks(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using (var from = zip.GetEntry(file.File)!.Open()) using (var to = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    var hash = Copy(from, to, file.Length, token, Report);
                    if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("VideoEditor.ArchiveChecksum");
                }
                File.SetLastWriteTimeUtc(path, file.LastWriteUtc);
            }
            foreach (var asset in p.Assets) {
                var sourceFile = members[asset.Path]; if (asset.Length != sourceFile.Length || asset.LastWriteUtc != sourceFile.LastWriteUtc) throw new InvalidDataException("VideoEditor.InvalidArchive");
                asset.Path = Path.Combine(directory, asset.Path.Replace('/', Path.DirectorySeparatorChar));
                if (asset.Proxy is {} proxy) asset.Proxy = Path.Combine(directory, proxy.Replace('/', Path.DirectorySeparatorChar));
            }
            for (var i = 0; i < p.ImportedFiles.Count; i++) {
                var attachment = p.ImportedFiles[i]; var file = members[attachment.Path];
                if (attachment.Length != file.Length || attachment.LastWriteUtc != file.LastWriteUtc) throw new InvalidDataException("VideoEditor.InvalidArchive");
                p.ImportedFiles[i] = attachment with { Path = Path.Combine(directory, attachment.Path.Replace('/', Path.DirectorySeparatorChar)) };
            }
            p.PreviewDirectory = Path.Combine(directory, "Preview"); p.Validate(); VideoSeries.Ensure(p); VideoProjectStore.VerifySources(p); return p;
        }
        catch {
            // This freshly-created directory is the only deletion target; never follow archive-supplied paths.
            if (Directory.Exists(directory)) { Lopata.Updates.SafeUpdatePath.RejectLinks(directory); Directory.Delete(directory, true); }
            throw;
        }
    }
    private static bool SafeMember(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 100 || path.Contains('\\') || path.Contains(':')) return false;
        var parts = path.Split('/'); return parts.Length == 2 && parts[0] is "media" or "Preview" && parts[1].Length > 0
            && parts[1] is not "." and not ".." && parts[1].All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-');
    }
}
