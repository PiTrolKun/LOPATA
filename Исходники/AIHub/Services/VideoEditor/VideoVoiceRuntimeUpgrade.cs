using System.IO;
using AIHub.Models;

namespace AIHub.Services.VideoEditor;

/// <summary>Reuse unchanged archives across revisions; the common acquisition service verifies copied bytes.</summary>
internal static class VideoVoiceRuntimeUpgrade
{
    internal static async Task ReuseAsync(ManagedModelArtifactCard? previous, ManagedModelArtifactCard current, CancellationToken token)
    {
        if (previous is null || !previous.IsManaged || previous.ModelArtifactId != current.ModelArtifactId
            || !current.ModelArtifactId.StartsWith("video-narrator-runtime-", StringComparison.Ordinal)
            || previous.Revision == current.Revision || string.IsNullOrWhiteSpace(current.ModelsRoot)
            || string.IsNullOrWhiteSpace(previous.ModelsRoot) || string.IsNullOrWhiteSpace(previous.InstallDirectory)
            || !Path.GetFullPath(previous.ModelsRoot).Equals(Path.GetFullPath(current.ModelsRoot), StringComparison.OrdinalIgnoreCase)) return;
        var allowed = Path.GetFullPath(current.ModelsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(previous.InstallDirectory).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) return;
        foreach (var file in current.Files)
        {
            token.ThrowIfCancellationRequested();
            if (!previous.Files.Any(old => old.RelativePath == file.RelativePath && old.SizeBytes == file.SizeBytes && old.Sha256 == file.Sha256)) continue;
            var source = RuntimeFileAudit.Contained(previous.InstallDirectory, file.RelativePath);
            var target = RuntimeFileAudit.Contained(current.InstallDirectory, file.RelativePath);
            Lopata.Updates.SafeUpdatePath.RejectLinks(source); Lopata.Updates.SafeUpdatePath.RejectLinks(target);
            if (!File.Exists(source) || File.Exists(target) || new FileInfo(source).Length != file.SizeBytes) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var staging = target + "." + Guid.NewGuid().ToString("N") + ".reuse";
            try
            {
                await using (var input = File.OpenRead(source))
                await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write))
                    await input.CopyToAsync(output, token);
                token.ThrowIfCancellationRequested(); File.Move(staging, target);
            }
            finally { if (File.Exists(staging)) File.Delete(staging); }
        }
    }
}
