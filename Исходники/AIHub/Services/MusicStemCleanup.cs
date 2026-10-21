using System.IO;

namespace AIHub.Services;

internal static class MusicStemCleanup
{
    internal static void Run(MusicGenerationJobs jobs, MusicGenerationJob job, int index, Action<string>? log)
    {
        var v = job.Variants[index];
        if (v.Stems is not { Published: true, Receipt: { } r }) return;
        var root = Path.GetFullPath(jobs.Folder(job.Id)) + Path.DirectorySeparatorChar;
        try {
            if (v.AudioFile is { } source && Path.GetFullPath(source).StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(source)) File.Delete(source);
            var currentStage = Path.GetDirectoryName(Path.GetDirectoryName(r.VoicePcm))!;
            var stages = job.Variants.All(x => x.Stems?.Published == true)
                ? Directory.EnumerateDirectories(jobs.Folder(job.Id), "stems-*", SearchOption.TopDirectoryOnly).ToArray() : [currentStage];
            foreach (var stage in stages) {
                var name = Path.GetFileName(stage);
                if (!name.StartsWith("stems-", StringComparison.Ordinal) || !Guid.TryParseExact(name[6..], "N", out _) ||
                    !Path.GetFullPath(stage).StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid private separation stage.");
                // Only owned stages; other variants retain their PCM until their own publication.
                if (Directory.Exists(stage)) {
                    if ((File.GetAttributes(stage) & FileAttributes.ReparsePoint) != 0 || Directory.EnumerateFileSystemEntries(stage, "*", SearchOption.AllDirectories)
                        .Any(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)) throw new IOException("Linked separation stage.");
                    Directory.Delete(stage, true);
                }
            }
        }
        catch (IOException error) { log?.Invoke("[Stems] Temporary cleanup deferred: " + error.Message); }
        catch (UnauthorizedAccessException error) { log?.Invoke("[Stems] Temporary cleanup deferred: " + error.Message); }
    }
}
