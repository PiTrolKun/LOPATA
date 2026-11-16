using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIHub.Models;
using Lopata.Updates;

namespace AIHub.Services;

internal sealed record CrashWorkspaceSnapshot(
    int Schema,
    CrashWorkspacePage Page,
    CrashWindowLayout MainWindow,
    bool WasInTray,
    CrashWorkspaceWindow[] Windows);

internal sealed record CrashWorkspacePage(string Id, string? Direction = null, string? Project = null,
    bool SettingsOpen = false, double? Scroll = null, string? File = null);

internal sealed record CrashWindowLayout(RememberedWindowPlacement Placement, int WindowState);

internal sealed record CrashWorkspaceWindow(string Kind, string? Project, string? File,
    double? Position, bool WasVisible, CrashWindowLayout Layout, bool Dirty = false);

/// <summary>Per-installation UI state. Operation and project data stay in their existing stores.</summary>
internal sealed class CrashWorkspaceStore(string path)
{
    private const int MaximumBytes = 256 * 1024;
    internal const int CurrentSchema = 1;

    internal static CrashWorkspaceStore ForCurrentInstallation()
    {
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        var identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(directory.ToUpperInvariant())));
        return new(Path.Combine(AppDataPaths.BaseDirectory, "Recovery", "Workspaces", identity, "workspace.json"));
    }

    internal CrashWorkspaceSnapshot? Load()
    {
        SafeUpdatePath.RejectLinks(path);
        var file = new FileInfo(path);
        if (!file.Exists) return null;
        if (file.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("Invalid recovery workspace size.");
        var snapshot = JsonSerializer.Deserialize<CrashWorkspaceSnapshot>(File.ReadAllBytes(path));
        if (snapshot is not { Schema: CurrentSchema, Page: not null, MainWindow.Placement: not null, Windows: not null }
            || snapshot.Windows.Length > 32
            || snapshot.Windows.Any(window => window is null || window.Layout?.Placement is null))
            throw new InvalidDataException("Invalid recovery workspace.");
        return snapshot;
    }

    internal void Save(byte[] bytes)
    {
        if (bytes.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("Invalid recovery workspace size.");
        SafeUpdatePath.RejectLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        SafeUpdatePath.RejectLinks(temporary);
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            SafeUpdatePath.RejectLinks(path);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
