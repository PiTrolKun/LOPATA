using System.IO;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Owns temporary imports; an exclusive lease protects other running instances.</summary>
public sealed class ImageAssetStore : IDisposable
{
    private readonly string _root;
    private string? _run;
    private FileStream? _lease;
    private bool _disposed;
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".part", ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp" };

    public ImageAssetStore(string temporaryRoot)
    {
        _root = Path.GetFullPath(temporaryRoot);
        Directory.CreateDirectory(_root);
        RejectLinks(_root);
        CleanupAbandoned();
    }

    public static string ImagesDirectory(StorageSettings settings)
    {
        var configured = settings.Results.Locations.Select(l => l.Path?.Trim()).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
        return Path.Combine(string.IsNullOrWhiteSpace(configured) ? AppDataPaths.RuntimeDirectory : Path.Combine(configured, "AI_HUB"), "Images");
    }

    public string Allocate(string extension)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Extensions.Contains(extension)) throw new ArgumentException("Unsupported temporary extension.");
        RejectLinks(_root);
        if (_run is null)
        {
            _run = Path.Combine(_root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_run);
            _lease = new FileStream(Path.Combine(_run, "owner.lease"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        }
        return Path.Combine(_run, Guid.NewGuid().ToString("N") + extension);
    }

    public string Keep(string temporaryPath, StorageSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Owns(temporaryPath)) throw new InvalidOperationException("Not an owned image.");
        var directory = Path.Combine(ImagesDirectory(settings), "Saved");
        Directory.CreateDirectory(directory);
        RejectLinks(directory);
        var destination = Path.Combine(directory, $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}{Path.GetExtension(temporaryPath)}");
        // Copy across volumes, then remove temporary source. Unique name never overwrites a duplicate.
        try { File.Copy(temporaryPath, destination, overwrite: false); }
        catch { if (File.Exists(destination)) File.Delete(destination); throw; }
        DeleteTemporary(temporaryPath);
        return destination;
    }

    public bool Owns(string path)
    {
        if (_run is null || string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            return string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), _run, StringComparison.OrdinalIgnoreCase)
                && IsImageName(path) && !IsLink(path) && !IsLink(_run) && !IsLink(_root);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return false; }
    }

    public void DeleteTemporary(string? path)
    {
        if (path is null || !Owns(path)) return;
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Retried on shutdown/startup. */ }
    }

    public void EndSession(ImageAnalysisLiterarySession? session)
    {
        if (session?.File?.StorageKind == ImageAssetKinds.Temporary) DeleteTemporary(session.File.SourcePath);
    }

    public void CleanupAbandoned()
    {
        RejectLinks(_root);
        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _) || IsLink(directory)) continue;
            var lease = Path.Combine(directory, "owner.lease");
            if (!File.Exists(lease) || IsLink(lease)) continue;
            try
            {
                using (new FileStream(lease, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    foreach (var file in Directory.EnumerateFiles(directory))
                        if (IsImageName(file) && !IsLink(file)) File.Delete(file);
                }
                // Never recurse: unfamiliar files/subdirectories remain untouched.
                if (Directory.EnumerateFileSystemEntries(directory).All(p => p == lease))
                { File.Delete(lease); Directory.Delete(directory); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Live owner or retry later. */ }
        }
    }

    private static bool IsImageName(string path) => Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _)
        && Extensions.Contains(Path.GetExtension(path));
    private static bool IsLink(string path) => (File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static void RejectLinks(string path)
    {
        for (var d = new DirectoryInfo(path); d is not null; d = d.Parent)
            if (d.Exists && (d.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked image storage is not supported.");
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lease?.Dispose();
        _lease = null;
        try { CleanupAbandoned(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

public static class ImageAssetKinds
{
    public const string External = "external";
    public const string Temporary = "temporary";
    public const string Saved = "saved";
}
