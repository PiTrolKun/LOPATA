using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lopata.Collection;

namespace Lopata.CollectionWorker;

internal static class WorkerHost
{
    public static Task<int> RunAsync(string root, bool diagnostic = false) => Task.Run(() => Run(root, diagnostic));

    private static int Run(string root, bool diagnostic)
    {
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..24];
        using var singleton = new Mutex(false, @"Local\LOPATA.CollectionWorker." + identity);
        bool acquired;
        try { acquired = singleton.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) return 0;
        var pidPath = Path.Combine(root, "worker.pid");
        var heartbeatPath = Path.Combine(root, "heartbeat");
        var quitPath = Path.Combine(root, "quit.request");
        try
        {
            if (!IsEnabled(root) || !IsExpectedWorker(root)) return 0;
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, "leases"));
            var started = DateTime.UtcNow;
            var pid = new byte[12];
            BitConverter.GetBytes((uint)Environment.ProcessId).CopyTo(pid, 0);
            BitConverter.GetBytes((ulong)Process.GetCurrentProcess().StartTime.ToUniversalTime().ToFileTimeUtc()).CopyTo(pid, 4);
            CollectionFiles.WriteAtomic(pidPath, pid);
            using var heartbeat = new Timer(_ => Touch(heartbeatPath), null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
            using var scanner = new CollectionScanner();
            var lastActive = DateTime.UtcNow;
            var previousRequest = DateTime.MinValue;
            var lastScan = DateTime.MinValue;
            while (IsEnabled(root) && IsExpectedWorker(root))
            {
                var now = DateTime.UtcNow;
                if (File.Exists(quitPath) && File.GetLastWriteTimeUtc(quitPath) >= started) return 0;
                if (HasLease(root)) lastActive = now;
                if (now - lastActive >= TimeSpan.FromSeconds(60)) return 0;
                var request = File.GetLastWriteTimeUtc(Path.Combine(root, "refresh.request"));
                if (lastScan == DateTime.MinValue || request != previousRequest || now - lastScan >= TimeSpan.FromSeconds(20))
                {
                    var config = CollectionConfig.Load(root);
                    if (!config.Enabled) return 0;
                    var revision = CollectionPublication.Revision(config);
                    var oldItems = CollectionPublication.ReadCurrent(root, revision);
                    Status(root, "updating", oldItems.Count, null, false);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                    var scan = Task.Run(async () =>
                    {
                        await WorkerAudit.VerifyAsync(root, config, diagnostic, timeout.Token);
                        // Reconcile the current snapshot before querying any discovery provider.
                        var available = CollectionPublication.FilterAvailable(config, oldItems, timeout.Token);
                        if (!Publish(root, revision, available)) return new CollectionScanResult([], null, false, 0);
                        return scanner.Scan(root, config, timeout.Token,
                            local => PublishLocal(root, config, revision, local, available), available);
                    }, timeout.Token);
                    // A blocked OS provider belongs to this expendable worker, never to Explorer.
                    var scanWait = Stopwatch.StartNew();
                    while (!scan.Wait(200))
                    {
                        if (!IsEnabled(root) || !IsExpectedWorker(root) || File.Exists(quitPath) && File.GetLastWriteTimeUtc(quitPath) >= started)
                        {
                            timeout.Cancel();
                            return 0;
                        }
                        if (scanWait.Elapsed >= TimeSpan.FromSeconds(50))
                        {
                            Status(root, "unavailable", CollectionPublication.ReadCurrent(root, revision).Count, "scan-timeout", true);
                            Environment.Exit(3);
                        }
                    }
                    var result = scan.GetAwaiter().GetResult();
                    if (CollectionPublication.IsCurrent(root, revision))
                    {
                        if (Publish(root, revision, result.Items))
                            Status(root, result.Error is null && !result.Limited ? "ready" : "partial", result.Items.Count, result.Error, result.Limited);
                    }
                    else { previousRequest = DateTime.MinValue; lastScan = DateTime.MinValue; continue; }
                    previousRequest = request;
                    lastScan = DateTime.UtcNow;
                }
                Thread.Sleep(1000);
            }
            return 0;
        }
        catch (Exception error) when (IsSuperseded(error)) { return 0; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or InvalidDataException or OperationCanceledException or AggregateException)
        {
            Status(root, "unavailable", SnapshotForDisplay(root).Count, error.GetBaseException().GetType().Name, true);
            return 2;
        }
        finally
        {
            try
            {
                var pid = File.ReadAllBytes(pidPath);
                if (pid.Length == 12 && BitConverter.ToUInt32(pid) == Environment.ProcessId)
                {
                    File.Delete(pidPath);
                    File.Delete(heartbeatPath);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            singleton.ReleaseMutex();
        }
    }

    internal static bool IsSuperseded(Exception error) => error is CollectionWorkerSupersededException
        || error is AggregateException group && group.Flatten().InnerExceptions.Count > 0
            && group.Flatten().InnerExceptions.All(e => e is CollectionWorkerSupersededException);

    private static bool HasLease(string root)
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "leases"), "*.lease").Take(128))
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromSeconds(15)) return true;
        return false;
    }

    private static bool IsEnabled(string root)
    {
        try
        {
            var path = Path.Combine(root, "enabled.bin");
            if (!File.Exists(path)) return false;
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> flag = stackalloc byte[4]; input.ReadExactly(flag);
            return BitConverter.ToUInt32(flag) == 1 && input.Length == 4;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool IsExpectedWorker(string root)
    {
        var expected = CollectionConfig.Load(root).ExpectedWorkerPath;
        return expected.Length == 0 || Environment.ProcessPath is { } current
            && Path.GetFullPath(expected).Equals(Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase);
    }

    private static void Touch(string path)
    {
        try { CollectionFiles.WriteAtomic(path, Encoding.ASCII.GetBytes(Environment.ProcessId.ToString())); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static void PublishLocal(string root, CollectionConfig config, string revision, IReadOnlyList<CollectionItem> local, IReadOnlyList<CollectionItem> previous)
    {
        if (!CollectionPublication.IsCurrent(root, revision)) return;
        var combined = CollectionPublication.MergeValidated(config, local, previous, root);
        Publish(root, revision, combined);
    }

    // Explorer's lease monitor observes snapshot changes and refreshes its view.
    private static bool Publish(string root, string revision, IReadOnlyList<CollectionItem> items) =>
        CollectionPublication.TryPublish(root, revision, items, Environment.ProcessPath ?? "");

    private static IReadOnlyList<CollectionItem> SnapshotForDisplay(string root)
    {
        try { return CollectionSnapshot.Read(root); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { return []; }
    }

    private static void Status(string root, string phase, int count, string? error, bool limited)
    {
        try
        {
            CollectionFiles.WriteAtomic(Path.Combine(root, "status.json"), JsonSerializer.SerializeToUtf8Bytes(new
            { schema = 1, phase, count, error, limited, checkedUtc = DateTimeOffset.UtcNow, pid = Environment.ProcessId }, CollectionFiles.Json));
            var state = phase switch { "updating" => 1U, "ready" => 2U, "partial" => 3U, _ => 4U };
            var binary = new byte[8];
            BitConverter.GetBytes(state).CopyTo(binary, 0);
            BitConverter.GetBytes(limited ? 1U : 0U).CopyTo(binary, 4);
            var path = Path.Combine(root, "status.bin");
            if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(binary)) CollectionFiles.WriteAtomic(path, binary);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
    }

}
