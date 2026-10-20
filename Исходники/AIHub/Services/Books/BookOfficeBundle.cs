using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace AIHub.Services.Books;

/// <summary>Unpacks the shipped office image into a private writable cache, without weakening updater paths.</summary>
internal static class BookOfficeBundle
{
    internal const string FileName = "libreoffice.bundle.zip";
    private sealed record Entry(string Name, long Bytes);

    internal static async Task EnsureAsync(string archivePath, string destination, Stream manifest, CancellationToken token)
    {
        using var document = await JsonDocument.ParseAsync(manifest, cancellationToken: token);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(document.RootElement);
        var expected = document.RootElement.GetProperty("Files").EnumerateArray()
            .Select(value => new Entry(value.GetProperty("Name").GetString()!, value.GetProperty("Bytes").GetInt64())).ToArray();
        if (expected.Length == 0 || expected.Any(row => row.Bytes < 0)) throw Invalid();
        var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in expected)
        {
            ValidateName(row.Name);
            if (!entries.TryAdd(row.Name, row)) throw Invalid();
        }
        destination = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar);
        if (destination.TrimEnd(Path.DirectorySeparatorChar).Length <= Path.GetPathRoot(destination)!.Length) throw Invalid();
        var parent = Path.GetDirectoryName(destination)!;
        Lopata.Updates.SafeUpdatePath.RejectLinks(parent);
        Directory.CreateDirectory(parent);
        await using var lease = await LockAsync(destination + ".lock", token);
        Lopata.Updates.SafeUpdatePath.RejectLinks(destination);
        if (Directory.Exists(destination))
        {
            await BookConverterRuntime.VerifyOfficeFilesAsync(destination, new MemoryStream(manifestBytes), token);
            return;
        }
        var stage = destination + ".preparing-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(stage);
        try
        {
            Lopata.Updates.SafeUpdatePath.RejectLinks(archivePath);
            using var zip = ZipFile.OpenRead(archivePath);
            if (zip.Entries.Count != entries.Count) throw Invalid();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var buffer = new byte[131072];
            foreach (var entry in zip.Entries)
            {
                token.ThrowIfCancellationRequested(); ValidateName(entry.FullName);
                var mode = (entry.ExternalAttributes >> 16) & 0xf000;
                if (mode is not (0 or 0x8000) || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0
                    || !seen.Add(entry.FullName) || !entries.TryGetValue(entry.FullName, out var row)
                    || entry.Length != row.Bytes) throw Invalid();
                var path = Path.GetFullPath(Path.Combine(stage, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (!path.StartsWith(stage + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw Invalid();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using var input = entry.Open();
                await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, true);
                long written = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, token)) > 0)
                {
                    written = checked(written + read);
                    if (written > row.Bytes) throw Invalid();
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                }
                if (written != row.Bytes) throw Invalid();
            }
            await BookConverterRuntime.VerifyOfficeFilesAsync(stage, new MemoryStream(manifestBytes), token);
            token.ThrowIfCancellationRequested(); Directory.Move(stage, destination);
        }
        finally
        {
            if (Directory.Exists(stage))
            {
                // Only this fresh, unpublished stage belongs to this attempt; existing cache is never replaced.
                if (!stage.StartsWith(destination + ".preparing-", StringComparison.OrdinalIgnoreCase)) throw Invalid();
                Lopata.Updates.SafeUpdatePath.RejectLinks(stage); Directory.Delete(stage, true);
            }
        }
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Contains('\\') || name.Any(c => c < 32 || "<>:\"|?*".Contains(c))
            || name.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' '))) throw Invalid();
        // Names are additionally required to match the immutable embedded manifest exactly.
        // "models" here is an upstream Python package inside this private component, not a user model store.
    }
    private static async Task<FileStream> LockAsync(string path, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested(); Lopata.Updates.SafeUpdatePath.RejectLinks(path);
            try { return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(100, token); }
        }
    }
    private static InvalidDataException Invalid() => new("BookConverter.Error.OfficeBundleInvalid");
}
