using System.IO;
using System.IO.Compression;
using System.Text.Json;
using AIHub.Services.VideoEditor;
using Lopata.Updates;

namespace AIHub.Services.VideoPreparation;

public static partial class VideoPreparationArchive
{
    public static VideoPreparationProject Load(string source, string workspace, CancellationToken token = default)
    {
        try { return LoadCore(source, workspace, token); }
        catch (Exception error) when (error is JsonException or OverflowException || error is InvalidDataException && !error.Message.StartsWith("VideoPrep.", StringComparison.Ordinal))
        { throw new InvalidDataException("VideoPrep.InvalidArchive", error); }
    }

    private static VideoPreparationProject LoadCore(string source, string workspace, CancellationToken token)
    {
        SafeUpdatePath.RejectLinks(Path.GetFullPath(source));
        using var zip = ZipFile.OpenRead(source);
        var manifest = ReadManifest(zip);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries) if (!names.Add(entry.FullName)) throw new InvalidDataException("VideoPrep.InvalidArchive");
        if (zip.Entries.Count != manifest.Files.Length + 1) throw new InvalidDataException("VideoPrep.InvalidArchive");
        var members = new Dictionary<string, Member>(StringComparer.Ordinal); long total = 0;
        foreach (var file in manifest.Files)
        {
            if (file is null || !SafeMember(file.File) || !members.TryAdd(file.File, file) || file.Length < 0
                || file.Sha256 is null || file.Sha256.Length != 64 || file.Sha256.Any(c => !char.IsAsciiHexDigit(c))
                || zip.GetEntry(file.File) is not { } entry || entry.Length != file.Length) throw new InvalidDataException("VideoPrep.InvalidArchive");
            total = checked(total + file.Length);
        }
        var project = manifest.Project;
        if (project.Attachments.Any(a => !members.ContainsKey(a.Path))) throw new InvalidDataException("VideoPrep.InvalidArchive");
        token.ThrowIfCancellationRequested();
        var directory = Path.Combine(Path.GetFullPath(workspace), Guid.NewGuid().ToString("N"));
        SafeUpdatePath.RejectLinks(directory); Directory.CreateDirectory(directory);
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(directory)!);
            if (drive.IsReady && drive.AvailableFreeSpace < checked(total + ManifestLimit)) throw new IOException("VideoPrep.DiskSpace");
            foreach (var member in manifest.Files)
            {
                token.ThrowIfCancellationRequested();
                var path = Path.Combine(directory, member.File.Replace('/', Path.DirectorySeparatorChar));
                SafeUpdatePath.RejectLinks(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var input = zip.GetEntry(member.File)!.Open();
                using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                if (!VideoProjectArchive.Copy(input, output, member.Length, token).Equals(member.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("VideoPrep.Checksum");
            }
            for (var i = 0; i < project.Attachments.Count; i++)
                project.Attachments[i] = project.Attachments[i] with { Path = Path.Combine(directory, project.Attachments[i].Path.Replace('/', Path.DirectorySeparatorChar)) };
            project.Validate(); return project;
        }
        catch
        {
            // Only this uniquely created extraction directory, never an archive-provided path.
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
            throw;
        }
    }

    private static bool SafeMember(string? name) => name is not null && name.StartsWith("media/", StringComparison.Ordinal)
        && name.Length > 6 && name[6..].All(c => char.IsAsciiLetterOrDigit(c) || c == '.') && !name[6..].Contains("..", StringComparison.Ordinal);
}
