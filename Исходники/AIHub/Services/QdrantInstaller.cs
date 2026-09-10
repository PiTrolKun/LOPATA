using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

namespace AIHub.Services;

public static class QdrantInstaller
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public static async Task<bool> IsInstalledAsync(QdrantOptions options, CancellationToken token)
    {
        if (!File.Exists(options.Executable)) return false;
        await using var input = File.OpenRead(options.Executable);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(input, token));
        return actual.Equals(QdrantOptions.ExecutableSha256, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task InstallAsync(QdrantOptions options, IProgress<double>? progress, CancellationToken token)
    {
        await ComponentLicenseGate.EnsureAsync(QdrantOptions.LicenseId, token);
        await Gate.WaitAsync(token);
        try
        {
            if (await IsInstalledAsync(options, token)) return;
            var parent = Path.GetDirectoryName(options.InstallDirectory)!;
            Directory.CreateDirectory(parent);
            // Exclusive file lock covers another running copy of the application.
            await using var lease = new FileStream(Path.Combine(parent, ".install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (await IsInstalledAsync(options, token)) return;
            var stage = Path.Combine(parent, "install-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            try
            {
                var archive = Path.Combine(stage, "download.zip");
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                using var response = await http.GetAsync(QdrantOptions.DownloadUri, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                await using (var source = await response.Content.ReadAsStreamAsync(token))
                await using (var output = File.Create(archive))
                {
                    var buffer = new byte[128 * 1024]; long total = 0; int read;
                    while ((read = await source.ReadAsync(buffer, token)) != 0)
                    {
                        total += read;
                        if (total > QdrantOptions.ArchiveBytes) throw new InvalidDataException("Qdrant archive exceeds pinned size.");
                        await output.WriteAsync(buffer.AsMemory(0, read), token);
                        progress?.Report(100d * total / QdrantOptions.ArchiveBytes);
                    }
                    if (total != QdrantOptions.ArchiveBytes) throw new InvalidDataException("Incomplete Qdrant archive.");
                }
                await using (var file = File.OpenRead(archive))
                    if (!Convert.ToHexString(await SHA256.HashDataAsync(file, token)).Equals(QdrantOptions.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Qdrant archive SHA256 mismatch.");
                using (var zip = ZipFile.OpenRead(archive))
                {
                    var executable = zip.Entries.Single(e => e.FullName == "qdrant.exe");
                    executable.ExtractToFile(Path.Combine(stage, "qdrant.exe"));
                }
                var license = Path.Combine(AppContext.BaseDirectory, "Licenses", "texts", "qdrant-LICENSE.txt");
                File.Copy(license, Path.Combine(stage, "LICENSE.txt"));
                await using (var file = File.OpenRead(Path.Combine(stage, "qdrant.exe")))
                    await File.WriteAllTextAsync(Path.Combine(stage, "qdrant.sha256"), Convert.ToHexString(await SHA256.HashDataAsync(file, token)), token);
                File.Delete(archive);
                token.ThrowIfCancellationRequested();
                // Preserve an incomplete/changed old installation for inspection, never touch its data directory.
                if (Directory.Exists(options.InstallDirectory)) Directory.Move(options.InstallDirectory, options.InstallDirectory + ".previous-" + Guid.NewGuid().ToString("N"));
                Directory.Move(stage, options.InstallDirectory);
            }
            finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
        }
        finally { Gate.Release(); }
    }
}
