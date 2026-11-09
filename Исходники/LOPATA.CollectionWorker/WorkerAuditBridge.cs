using System.Text.Json;
using Lopata.Collection;

namespace AIHub.Services;

// The shared ComponentAudit implementation uses these host services. This bridge only
// supplies storage and reporting; the receipt, invalidation and policy logic is linked unchanged.
internal static class WorkerAuditBridge
{
    internal static string DataRoot { get; private set; } = CollectionPaths.DefaultDirectory;
    internal static void Configure(string root)
    {
        DataRoot = Path.GetFullPath(root);
        AppDataPaths.BaseDirectory = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(DataRoot))
            ?? throw new InvalidDataException("Invalid collection data root.");
    }
}

internal static class AppDataPaths
{
    internal static string BaseDirectory { get; set; } = Path.GetDirectoryName(CollectionPaths.DefaultDirectory)!;
}

internal static class OwnedProcessRegistry
{
    private static readonly object Gate = new();
    internal static void Log(string action, string owner, string? detail = null)
    {
        try
        {
            lock (Gate)
            {
                var path = Path.Combine(WorkerAuditBridge.DataRoot, "component-audit.log");
                Directory.CreateDirectory(WorkerAuditBridge.DataRoot);
                if (File.Exists(path) && new FileInfo(path).Length > 64 * 1024) File.Delete(path);
                File.AppendAllText(path, JsonSerializer.Serialize(new { action, owner, detail, utc = DateTimeOffset.UtcNow }) + "\n");
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
    }
}

internal static class ComponentAuditFailures
{
    internal static void Report(Exception error)
    {
        if (ComponentAudit.Enabled || error is OperationCanceledException) return;
        try
        {
            CollectionFiles.WriteAtomic(Path.Combine(WorkerAuditBridge.DataRoot, "component-audit-failure.json"),
                JsonSerializer.SerializeToUtf8Bytes(new
                { schema = 1, component = "explorer-collection", failure = error.GetType().Name, utc = DateTimeOffset.UtcNow }));
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
    }
}
