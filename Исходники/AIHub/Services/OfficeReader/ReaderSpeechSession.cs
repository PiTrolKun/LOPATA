using System.IO;

namespace AIHub.Services.OfficeReader;

/// <summary>Only document-owned temporary audio; saved copies live outside this directory.</summary>
internal sealed class ReaderSpeechSession : IDisposable
{
    internal static string DefaultRoot => Path.Combine(AppDataPaths.BaseDirectory, "OfficeReader", "Speech", "Temporary");
    internal string Id { get; } = Guid.NewGuid().ToString("N");
    internal string DirectoryPath { get; }
    internal CancellationToken Token => _lifetime.Token;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _root;
    private readonly FileStream _lease;
    private bool _disposed;

    internal ReaderSpeechSession(string? root = null)
    {
        _root = Path.GetFullPath(root ?? DefaultRoot);
        DirectoryPath = Path.Combine(_root, Id);
        Lopata.Updates.SafeUpdatePath.RejectLinks(DirectoryPath);
        Directory.CreateDirectory(DirectoryPath);
        _lease = new FileStream(Path.Combine(DirectoryPath, ".lease"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
    }

    internal void Cancel() { if (!_disposed) _lifetime.Cancel(); }
    public void Dispose()
    {
        if (_disposed) return;
        _lifetime.Cancel(); _lease.Dispose(); _disposed = true; _lifetime.Dispose();
        Delete(_root, DirectoryPath);
    }

    internal static void RemoveOrphans(string? root = null)
    {
        var directory = Path.GetFullPath(root ?? DefaultRoot);
        Lopata.Updates.SafeUpdatePath.RejectLinks(directory);
        if (!Directory.Exists(directory)) return;
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (!Guid.TryParseExact(Path.GetFileName(child), "N", out _)) continue;
            try
            {
                Lopata.Updates.SafeUpdatePath.RejectLinks(child);
                // An open reader holds an exclusive lease; never touch its files.
                using (new FileStream(Path.Combine(child, ".lease"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) { }
                Delete(directory, child);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { OwnedProcessRegistry.Log("reader_speech_cleanup_deferred", "Office.Reader.Speech", detail: e.GetType().Name); }
        }
    }

    private static void Delete(string root, string path)
    {
        var full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(full), root, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(full), "N", out _)) throw new InvalidDataException("Reader.Speech.Failed");
        Lopata.Updates.SafeUpdatePath.RejectLinks(full);
        try { if (Directory.Exists(full)) Directory.Delete(full, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { OwnedProcessRegistry.Log("reader_speech_cleanup_deferred", "Office.Reader.Speech", detail: e.GetType().Name); }
    }
}
