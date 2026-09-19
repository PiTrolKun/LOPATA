using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIHub.Services;

/// <summary>Reuses a successful digest only while the file and expected digest are unchanged.</summary>
public sealed class LiteraryFileVerification(string receiptDirectory)
{
    public static LiteraryFileVerification Shared { get; } = new(Path.Combine(AppDataPaths.BaseDirectory, "Literary", "Verification"));
    private readonly SemaphoreSlim _gate = new(1, 1);
    private sealed record Receipt(int Version, string Path, long Size, long Written, long Created, string Algorithm, string Hash);

    public async Task<bool> ValidAsync(string path, long size, string hash, string algorithm, CancellationToken ct,
        bool force = false, IProgress<double>? progress = null)
    {
        ct.ThrowIfCancellationRequested();
        if (algorithm is not ("sha256" or "gitsha1")) throw new ArgumentException("Unsupported digest.", nameof(algorithm));
        path = Path.GetFullPath(path);
        var receiptPath = Path.Combine(receiptDirectory,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant()))) + ".json");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != size) { Invalidate(receiptPath); return false; }
            // Keep normal writers and replacements out while comparing metadata and hashing.
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                1048576, FileOptions.Asynchronous | FileOptions.SequentialScan);
            info.Refresh();
            if (stream.Length != size || info.Length != size) { Invalidate(receiptPath); return false; }
            var current = new Receipt(1, path.ToUpperInvariant(), size, info.LastWriteTimeUtc.Ticks,
                info.CreationTimeUtc.Ticks, algorithm, hash.ToUpperInvariant());
            if (!force && ReadReceipt(receiptPath) == current)
            {
                ct.ThrowIfCancellationRequested(); progress?.Report(100); return true;
            }
            // A failed or cancelled recheck must never leave the previous success on disk.
            Invalidate(receiptPath);
            progress?.Report(0);
            using var digest = IncrementalHash.CreateHash(algorithm == "gitsha1" ? HashAlgorithmName.SHA1 : HashAlgorithmName.SHA256);
            if (algorithm == "gitsha1") digest.AppendData(Encoding.UTF8.GetBytes($"blob {size}\0"));
            var buffer = new byte[1048576]; long read = 0; var lastPercent = 0;
            int count;
            while ((count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                digest.AppendData(buffer, 0, count); read += count;
                var percent = (int)(100d * read / Math.Max(1, size));
                if (percent > lastPercent) { progress?.Report(percent); lastPercent = percent; }
            }
            ct.ThrowIfCancellationRequested();
            if (read != size || !Convert.ToHexString(digest.GetHashAndReset()).Equals(hash, StringComparison.OrdinalIgnoreCase)) return false;
            WriteReceipt(receiptPath, current);
            progress?.Report(100); return true;
        }
        finally { _gate.Release(); }
    }

    private static Receipt? ReadReceipt(string path)
    {
        try { return JsonSerializer.Deserialize<Receipt>(File.ReadAllText(path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static void Invalidate(string path)
    {
        // If an existing receipt cannot be removed, report the error instead of retaining stale success.
        if (File.Exists(path)) File.Delete(path);
    }

    private static void WriteReceipt(string path, Receipt receipt)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(receipt));
            File.Move(temporary, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { /* Caching is optional; the digest itself has already succeeded. */ }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
