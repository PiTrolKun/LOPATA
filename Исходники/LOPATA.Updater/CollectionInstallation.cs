using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Lopata.Updates;

namespace Lopata.Updater;

internal sealed class CollectionInitializationException(string message, Exception? inner = null) : IOException(message, inner);
internal sealed class CollectionChildExitException(int exitCode, string diagnostic)
    : Exception("Collection initializer exited with code " + exitCode + ".\n" + diagnostic)
{ internal int ExitCode { get; } = exitCode; }

/// <summary>Initializes Explorer integration after the installed signed manifest has been registered.</summary>
internal static class CollectionInstallation
{
    internal static async Task InitializeAsync(InstalledUpdateState installation, SignedManifest signed,
        IReadOnlyDictionary<string, string> keys, Func<string, string> text, CancellationToken token = default,
        Func<ProcessStartInfo, CancellationToken, Task<int>>? runner = null, TimeSpan? maximumDuration = null)
    {
        token.ThrowIfCancellationRequested();
        var manifest = signed.Verify(keys);
        var entry = manifest.Files.SingleOrDefault(f => f.Root == "app" && f.Path == "AIHub.exe")
            ?? throw new InvalidDataException("The signed installation does not contain the application initializer.");
        // Bootstrap-v1 predates the collection and can legitimately install older signed apps.
        // Only their complete absence of collection files permits skipping the new command.
        if (!manifest.Files.Any(f => f.Root == "app" && f.Path.StartsWith("Collection/", StringComparison.OrdinalIgnoreCase))) return;
        if (!manifest.Files.Any(f => f.Root == "app" && f.Path.Equals("Collection/current.json", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The signed collection payload is missing its current manifest.");
        var path = installation.Roots().Resolve(entry);
        SafeUpdatePath.RejectLinks(path);
        var duration = maximumDuration ?? TimeSpan.FromMinutes(2);
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(2)) throw new ArgumentOutOfRangeException(nameof(maximumDuration));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(duration);
        try
        {
            // Keep the verified executable closed to replacement until its child has exited.
            await using var verified = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (verified.Length != entry.Size || Convert.ToHexStringLower(await SHA256.HashDataAsync(verified, deadline.Token)) != entry.Sha256)
                throw new InvalidDataException("The installed application initializer failed signed-file verification.");
            var start = new ProcessStartInfo(path)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetFullPath(installation.AppDirectory),
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add("--initialize-collection");
            var exit = await (runner ?? RunChildAsync)(start, deadline.Token).WaitAsync(deadline.Token);
            if (exit != 0)
                throw new CollectionInitializationException(string.Format(CultureInfo.InvariantCulture,
                    text("UpdateHost.CollectionInitializationFailed"), exit));
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested)
        { throw new CollectionInitializationException(text("UpdateHost.CollectionInitializationTimedOut"), error); }
        catch (CollectionChildExitException error)
        { throw new CollectionInitializationException(string.Format(CultureInfo.InvariantCulture,
            text("UpdateHost.CollectionInitializationFailed"), error.ExitCode), error); }
        catch (CollectionInitializationException) { throw; }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception)
        { throw new CollectionInitializationException(text("UpdateHost.CollectionInitializationStartFailed"), error); }
    }

    private static async Task<int> RunChildAsync(ProcessStartInfo start, CancellationToken token)
    {
        using var process = Process.Start(start) ?? throw new IOException("The installed application initializer did not start.");
        using var cancellation = token.Register(() => StopOwnChild(process));
        var output = DrainAsync(process.StandardOutput, token);
        var error = ReadBoundedAsync(process.StandardError, token, true);
        var exit = process.WaitForExitAsync(token);
        var pending = new List<Task> { output, error, exit };
        try
        {
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending).WaitAsync(token);
                await completed;
                pending.Remove(completed);
            }
            if (process.ExitCode != 0) throw new CollectionChildExitException(process.ExitCode, await error);
            return 0;
        }
        finally
        {
            StopOwnChild(process);
            try { await Task.WhenAll(output, error, exit).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception cleanup) when (cleanup is IOException or OperationCanceledException or TimeoutException or InvalidOperationException) { }
        }
    }

    internal static async Task DrainAsync(TextReader reader, CancellationToken token) =>
        _ = await ReadBoundedAsync(reader, token, false);

    internal static async Task<string> ReadBoundedAsync(TextReader reader, CancellationToken token, bool capture)
    {
        var buffer = new char[2048]; var total = 0;
        var value = capture ? new StringBuilder() : null;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            if ((total += count) > 64 * 1024) throw new InvalidDataException("The collection initializer output exceeded its limit.");
            value?.Append(buffer, 0, count);
        }
        return value?.ToString() ?? "";
    }

    private static void StopOwnChild(Process process)
    {
        try { if (!process.HasExited) process.Kill(); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
    }
}
