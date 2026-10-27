using System.IO;
using System.IO.Compression;
using System.Text.Json;
using AIHub.Services.VideoEditor;
using Lopata.Updates;

namespace AIHub.Services.VideoPreparation;

/// <summary>Portable ZIP with a distinct schema, using the editor's bounded streaming/checksum primitive.</summary>
public static partial class VideoPreparationArchive
{
    public const string Extension = ".lopataAIVid";
    private const long ManifestLimit = 32L * 1024 * 1024;
    private const string Format = "LOPATA.AIVideoPreparation";
    private sealed record Member(string File, long Length, string Sha256);
    private sealed record Manifest(string Format, int Schema, VideoPreparationProject Project, Member[] Files);

    public static void Save(VideoPreparationProject project, string destination, CancellationToken token = default)
    {
        project.Validate(); token.ThrowIfCancellationRequested();
        var path = Path.GetFullPath(destination);
        if (!path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) throw new IOException("VideoPrep.InvalidExtension");
        SafeUpdatePath.RejectLinks(path);
        if (project.Attachments.Any(a => Path.GetFullPath(a.Path).Equals(path, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("VideoPrep.ProtectedFile");
        if (File.Exists(path))
        {
            using var existing = ZipFile.OpenRead(path);
            if (ReadManifest(existing).Project.Id != project.Id) throw new IOException("VideoPrep.ProtectedFile");
        }
        var copy = project.Clone();
        copy.Provenance = project.Provenance with { ProgramVersion = new OutputProvenance().ProgramVersion,
            ExportedUtc = DateTimeOffset.UtcNow, CreatedUtc = project.CreatedUtc };
        if (copy.Development is not null || copy.World is not null || copy.Shots is not null) VideoTranslationHistory.UpdateProvenance(copy);
        _ = copy.Provenance.Json();
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < copy.Attachments.Count; i++)
        {
            var attachment = project.Attachments[i]; var source = Path.GetFullPath(attachment.Path);
            SafeUpdatePath.RejectLinks(source);
            if (!File.Exists(source)) throw new IOException("VideoPrep.MissingAttachment");
            if (!sources.TryGetValue(source, out var member))
            {
                var extension = Path.GetExtension(source);
                if (extension.Length > 16 || extension.Skip(1).Any(c => !char.IsAsciiLetterOrDigit(c))) extension = ".bin";
                sources.Add(source, member = "media/" + sources.Count.ToString("D5", System.Globalization.CultureInfo.InvariantCulture) + extension);
            }
            copy.Attachments[i] = attachment with { Path = member };
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
                {
                    var members = new List<Member>();
                    foreach (var pair in sources)
                    {
                        token.ThrowIfCancellationRequested();
                        var before = new FileInfo(pair.Key); var length = before.Length; var modified = before.LastWriteTimeUtc;
                        using var input = new FileStream(pair.Key, FileMode.Open, FileAccess.Read, FileShare.Read);
                        using var target = zip.CreateEntry(pair.Value, CompressionLevel.NoCompression).Open();
                        var hash = VideoProjectArchive.Copy(input, target, length, token);
                        before.Refresh();
                        if (before.Length != length || before.LastWriteTimeUtc != modified) throw new IOException("VideoPrep.SourceChanged");
                        members.Add(new(pair.Value, length, hash));
                    }
                    var data = JsonSerializer.SerializeToUtf8Bytes(new Manifest(Format, 1, copy, members.ToArray()));
                    if (data.Length > ManifestLimit) throw new InvalidDataException("VideoPrep.InvalidArchive");
                    using var manifest = zip.CreateEntry("project.json", CompressionLevel.Optimal).Open(); manifest.Write(data);
                }
                output.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            SafeUpdatePath.RejectLinks(path);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static Manifest ReadManifest(ZipArchive zip)
    {
        var entry = zip.GetEntry("project.json") ?? throw new InvalidDataException("VideoPrep.InvalidArchive");
        if (entry.Length > ManifestLimit) throw new InvalidDataException("VideoPrep.InvalidArchive");
        using var input = entry.Open(); using var memory = new MemoryStream();
        VideoProjectArchive.Copy(input, memory, entry.Length, CancellationToken.None);
        var manifest = JsonSerializer.Deserialize<Manifest>(memory.ToArray()) ?? throw new InvalidDataException("VideoPrep.InvalidArchive");
        if (manifest.Format != Format || manifest.Schema != 1 || manifest.Project is null || manifest.Files is null
            || manifest.Files.Length > 100000) throw new InvalidDataException("VideoPrep.InvalidArchive");
        manifest.Project.Validate(); return manifest;
    }
}
