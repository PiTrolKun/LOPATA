using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIHub.Services;

/// <summary>One policy for installed component checks. Installations and explicit audits force full checks.</summary>
public static class ComponentAudit
{
    private sealed record Receipt(string Component, string Root, string Revision, string Generation, long Created, DateTimeOffset Verified);
    private sealed class Operation(bool enabled)
    {
        public bool Enabled { get; } = enabled;
        public ConcurrentDictionary<string, byte> Verified { get; } = new(StringComparer.Ordinal);
    }
    private sealed record Scope(string? Store = null, bool? Enabled = null, int Forced = 0,
        Operation? Operation = null, Action<string>? Log = null, bool Installing = false, bool FullCallback = false);
    private sealed class Restore(Scope? before) : IDisposable
    { public void Dispose() => Ambient.Value = before; }
    private static readonly AsyncLocal<Scope?> Ambient = new();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);
    private static readonly object StateGate = new();
    private static bool _enabled = true;
    public static bool Enabled => Ambient.Value?.Enabled ?? Volatile.Read(ref _enabled);
    public static bool FullRequired => Ambient.Value?.Forced > 0 || Ambient.Value?.FullCallback == true || (Ambient.Value?.Operation?.Enabled ?? Enabled);
    public static Func<string, string> Localize { get; set; } = key => key == "ComponentAudit.FirstRun"
        ? "First component launch — performing a full check" : key;
    public static event EventHandler? Changed;
    public static event Action<string>? Log;

    public static void Configure(bool enabled)
    {
        var changed = Volatile.Read(ref _enabled) != enabled;
        Volatile.Write(ref _enabled, enabled);
        if (changed) Changed?.Invoke(null, EventArgs.Empty);
    }
    public static IDisposable CaptureOperation()
    {
        var before = Ambient.Value;
        Ambient.Value = (before ?? new()) with { Operation = before?.Operation ?? new Operation(Enabled) };
        return new Restore(before);
    }
    public static IDisposable CaptureLog(Action<string>? log)
    {
        var before = Ambient.Value;
        Ambient.Value = (before ?? new()) with { Log = log ?? before?.Log };
        return new Restore(before);
    }
    public static IDisposable ForceFull()
    {
        var before = Ambient.Value;
        Ambient.Value = (before ?? new()) with { Forced = (before?.Forced ?? 0) + 1 };
        return new Restore(before);
    }
    public static IDisposable Installation()
    {
        var before = Ambient.Value;
        Ambient.Value = (before ?? new()) with { Forced = (before?.Forced ?? 0) + 1, Installing = true };
        return new Restore(before);
    }
    // Isolated test state flows with the async operation; it never touches user settings or receipts.
    public static IDisposable UseTestStore(string directory, bool? enabled = null)
    {
        var before = Ambient.Value;
        Ambient.Value = new(Path.GetFullPath(directory), enabled);
        return new Restore(before);
    }
    private static string Store => Ambient.Value?.Store ?? Path.Combine(AppDataPaths.BaseDirectory, "ComponentAudit");
    internal static string StoreDirectory => Store;
    private static string Root(string path)
    {
        var absolute = Path.GetFullPath(path);
        return absolute.Length == Path.GetPathRoot(absolute)?.Length ? absolute
            : absolute.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
    private static string Stamp(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static long Created(string root) => Directory.Exists(root) ? Directory.GetCreationTimeUtc(root).Ticks : -1;

    /// <summary>Invalidates a replaced installation, even when its public revision is unchanged.</summary>
    public static void Invalidate(string directory)
    {
        var root = Root(directory);
        lock (StateGate)
        {
            var generations = ReadGenerations();
            generations[root] = Guid.NewGuid().ToString("N");
            Atomic(Path.Combine(Store, "installations.json"), generations);
        }
    }
    private static Dictionary<string, string> ReadGenerations()
    {
        var path = Path.Combine(Store, "installations.json");
        if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("Component audit installation registry is oversized.");
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            if (data is null || data.Any(p => string.IsNullOrWhiteSpace(p.Key) || string.IsNullOrWhiteSpace(p.Value)))
                throw new InvalidDataException("Component audit installation registry is incomplete.");
            return new(data, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException)
        {
            // A fresh global epoch invalidates every old proof before recovering a damaged registry.
            var recovered = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["*"] = Guid.NewGuid().ToString("N") };
            Atomic(path, recovered);
            OwnedProcessRegistry.Log("component_audit_registry_recovered", "ComponentAudit", detail: error.GetType().Name);
            return recovered;
        }
    }
    private static string Generation(string root)
    {
        lock (StateGate)
            return Stamp(string.Join('\n', ReadGenerations().Where(p => p.Key == "*" || root.Equals(p.Key, StringComparison.OrdinalIgnoreCase)
                || root.StartsWith(p.Key.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Key.ToUpperInvariant() + "=" + p.Value)));
    }
    private static void Atomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(stream, value); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static Receipt? ReadReceipt(string path)
    {
        try
        {
            var receipt = File.Exists(path) && new FileInfo(path).Length <= 64 * 1024
                ? JsonSerializer.Deserialize<Receipt>(File.ReadAllText(path)) : null;
            return receipt is not null && !string.IsNullOrWhiteSpace(receipt.Component)
                && !string.IsNullOrWhiteSpace(receipt.Root) && !string.IsNullOrWhiteSpace(receipt.Revision)
                && !string.IsNullOrWhiteSpace(receipt.Generation) && receipt.Created >= 0
                && receipt.Verified > DateTimeOffset.UnixEpoch ? receipt : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }
    public static async Task RunAsync(string component, string directory, string revision,
        Func<CancellationToken, Task> presence, Func<CancellationToken, Task> full,
        CancellationToken token, Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(component);
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);
        using var operation = CaptureOperation();
        var scope = Ambient.Value!;
        var root = Root(directory);
        var key = Stamp(component + "\n" + root.ToUpperInvariant() + "\n" + revision);
        var gate = Gates.GetOrAdd(Store + "|" + key, _ => new SemaphoreSlim(1, 1));
        var receiptPath = Path.Combine(Store, "receipts", key + ".json");
        var fullStarted = false;
        await gate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            var generation = Generation(root);
            var created = Created(root);
            var identity = key + "|" + generation + "|" + created;
            var receipt = ReadReceipt(receiptPath);
            var known = receipt is not null && receipt.Component == component && receipt.Root.Equals(root, StringComparison.OrdinalIgnoreCase)
                && receipt.Revision == revision && receipt.Generation == generation && receipt.Created == created && created >= 0;
            if (!scope.Installing && (scope.Forced == 0 && scope.Operation!.Verified.ContainsKey(identity) || known && !FullRequired))
            {
                await presence(token);
                return;
            }
            if (!known)
            {
                var line = "[Audit] " + Localize("ComponentAudit.FirstRun") + " · " + component;
                (log ?? scope.Log)?.Invoke(line);
                Log?.Invoke(line);
                OwnedProcessRegistry.Log("component_audit_first_run", "ComponentAudit", detail: component);
            }
            fullStarted = true;
            var beforeFull = Ambient.Value;
            Ambient.Value = scope with { FullCallback = true };
            try { await full(token); }
            finally { Ambient.Value = beforeFull; }
            token.ThrowIfCancellationRequested();
            // Download/staging validation does not consume the mandatory first-use audit.
            if (scope.Installing) return;
            // A full callback may publish a freshly staged installation and invalidate the previous one.
            generation = Generation(root); created = Created(root);
            if (created < 0) throw new DirectoryNotFoundException("Component installation is missing: " + component);
            Atomic(receiptPath, new Receipt(component, root, revision, generation, created, DateTimeOffset.UtcNow));
            scope.Operation!.Verified.TryAdd(key + "|" + generation + "|" + created, 0);
        }
        catch (Exception error)
        {
            // A failed full audit must not leave an older proof authorizing quick reuse.
            if (fullStarted)
            {
                foreach (var verified in scope.Operation!.Verified.Keys.Where(value => value.StartsWith(key + "|", StringComparison.Ordinal)))
                    scope.Operation.Verified.TryRemove(verified, out _);
                try { File.Delete(receiptPath); }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                { OwnedProcessRegistry.Log("component_audit_receipt_cleanup_failed", "ComponentAudit", detail: component); }
            }
            ComponentAuditFailures.Report(error); throw;
        }
        finally { gate.Release(); }
    }
}
