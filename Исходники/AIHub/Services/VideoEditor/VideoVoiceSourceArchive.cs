using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using AIHub.Models;

namespace AIHub.Services.VideoEditor;

/// <summary>Project the pinned pure-Python SoX wrapper without executing its setup script or installing native SoX.</summary>
internal static class VideoVoiceSourceArchive
{
    internal static async Task<IReadOnlyList<string>> ProcessAsync(ManagedModelArtifactFile artifact, string source,
        string root, bool extract, CancellationToken token)
    {
        var (prefix, package) = artifact.RelativePath switch {
            "sox-1.5.0.tar.gz" => ("sox-1.5.0/sox/", "sox"),
            "openai-whisper-20231117.tar.gz" => ("openai-whisper-20231117/whisper/", "whisper"),
            "antlr4-python3-runtime-4.9.3.tar.gz" => ("antlr4-python3-runtime-4.9.3/src/antlr4/", "antlr4"),
            "docopt-0.6.2.tar.gz" => ("docopt-0.6.2/", "docopt"),
            _ => throw new InvalidDataException("Unsupported voice source archive.") };
        await using var file = File.OpenRead(source);
        if (file.Length != artifact.SizeBytes || !Convert.ToHexString(await SHA256.HashDataAsync(file, token)).Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Voice source archive integrity failure.");
        file.Position = 0; using var gzip = new GZipStream(file, CompressionMode.Decompress); using var tar = new TarReader(gzip);
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
        while (await tar.GetNextEntryAsync(cancellationToken: token) is {} entry)
        {
            if (entry.EntryType == TarEntryType.Directory) continue;
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) throw new InvalidDataException("Links are not allowed in voice source archives.");
            total += entry.Length; if (total > 16 * 1024 * 1024) throw new InvalidDataException("Oversized voice source archive.");
            var module = package == "docopt";
            var relative = module && entry.Name == prefix + "docopt.py" ? "Lib/site-packages/docopt.py"
                : !module && entry.Name.StartsWith(prefix, StringComparison.Ordinal) ? "Lib/site-packages/" + package + "/" + entry.Name[prefix.Length..]
                : entry.Name.EndsWith("/LICENSE", StringComparison.Ordinal) || entry.Name.EndsWith("/LICENSE-MIT", StringComparison.Ordinal) ? "Lib/site-packages/" + package + "-notices/LICENSE"
                : entry.Name.EndsWith("/PKG-INFO", StringComparison.Ordinal) && entry.Name.Count(c => c == '/') == 1 ? "Lib/site-packages/" + package + "-notices/PKG-INFO" : null;
            if (relative is null) continue;
            if (relative.Split('/').Any(part => part is "." or ".." || part.Contains('\\'))) throw new InvalidDataException("Unsafe voice source entry.");
            var path = RuntimeFileAudit.Contained(root, relative);
            if (!expected.Add(path)) throw new InvalidDataException("Ambiguous voice source entry: " + artifact.RelativePath + ": " + entry.Name);
            var payload = entry.DataStream ?? (entry.Length == 0 ? Stream.Null : throw new InvalidDataException("Missing voice source payload."));
            if (extract)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write); await payload.CopyToAsync(output, token);
            }
            else
            {
                await using var actual = File.OpenRead(path);
                var originalHash = await SHA256.HashDataAsync(payload, token); var actualHash = await SHA256.HashDataAsync(actual, token);
                if (actual.Length != entry.Length || !CryptographicOperations.FixedTimeEquals(originalHash, actualHash)) throw new InvalidDataException("Voice source file differs from its archive.");
            }
        }
        var required = package == "docopt" ? Path.Combine(root, "Lib", "site-packages", "docopt.py") : Path.Combine(root, "Lib", "site-packages", package, "__init__.py");
        if (!expected.Contains(required)) throw new InvalidDataException("Incomplete voice Python source archive.");
        return expected.ToArray();
    }
}
