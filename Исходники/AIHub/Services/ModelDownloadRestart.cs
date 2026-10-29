using System.IO;
using System.Security.Cryptography;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Stage a complete HTTP response without risking an earlier partial download.</summary>
internal static class ModelDownloadRestart
{
    internal static async Task ReplacePartialAsync(string partialPath, ManagedModelArtifactFile file,
        Func<string, Task> writeCompleteResponse, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("A pinned SHA256 is required for a safe download restart.");
        var root = Path.GetPathRoot(Path.GetFullPath(partialPath))!;
        if (new DriveInfo(root).AvailableFreeSpace < file.SizeBytes + 512L * 1024 * 1024)
            throw new IOException("Not enough free space to preserve the old partial file while downloading a complete replacement.");
        var staging = partialPath + ".restart-" + Guid.NewGuid().ToString("N");
        try
        {
            Lopata.Updates.SafeUpdatePath.RejectLinks(staging);
            using (new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            await writeCompleteResponse(staging);
            token.ThrowIfCancellationRequested();
            await using (var input = new FileStream(staging, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (input.Length != file.SizeBytes) throw new EndOfStreamException("The complete replacement download ended before the expected file size.");
                if (!Convert.ToHexString(await SHA256.HashDataAsync(input, token)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("SHA256 verification failed for the replacement download.");
            }
            token.ThrowIfCancellationRequested();
            Lopata.Updates.SafeUpdatePath.RejectLinks(partialPath);
            File.Move(staging, partialPath, overwrite: true);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }
}
