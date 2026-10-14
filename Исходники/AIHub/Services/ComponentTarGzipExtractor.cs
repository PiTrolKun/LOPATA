using System.Formats.Tar;
using System.IO;
using System.IO.Compression;

namespace AIHub.Services;

/// <summary>Unwrap tar.gz packages; a GZip archive alone exposes only the inner tar file.</summary>
internal static class ComponentTarGzipExtractor
{
    internal static void Extract(string archivePath, string destination, long maximumBytes, int maximumEntries)
    {
        var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var file = File.OpenRead(archivePath);
        using var gzip = new GZipStream(file,CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        long total = 0; int count = 0;
        while (reader.GetNextEntry(copyData:false) is { } entry)
        {
            if (++count > maximumEntries || entry.Length < 0 || entry.Length > maximumBytes-total)
                throw new InvalidDataException("Tar package exceeds extraction limits.");
            var name = entry.Name.Replace('\\','/').TrimEnd('/');
            if (name.Length == 0 || Path.IsPathRooted(name) || name.Contains(':') ||
                name.Split('/').Any(part=>part is "" or "." or "..") || !names.Add(name))
                throw new InvalidDataException("Unsafe or duplicate tar entry.");
            var target = Path.GetFullPath(Path.Combine(root,name));
            if (!target.StartsWith(root,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Tar entry escapes staging.");
            Lopata.Updates.SafeUpdatePath.RejectLinks(target);
            if (entry.EntryType == TarEntryType.Directory) { Directory.CreateDirectory(target); continue; }
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                throw new InvalidDataException("Tar links and special entries are not supported.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var output = new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None);
            long copied = 0;
            if (entry.DataStream is { } input) {
                var buffer = new byte[64*1024]; int read;
                while ((read = input.Read(buffer)) != 0) {
                    if (read > maximumBytes-total) throw new InvalidDataException("Tar package exceeds extraction limits.");
                    total += read; copied += read; output.Write(buffer,0,read);
                }
            }
            if (copied != entry.Length) throw new InvalidDataException("Truncated tar entry.");
        }
    }
}
