using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace Lopata.Recovery;

internal static class RecoverySupervisor
{
    private enum LaunchMode { Normal, Recovery, Safe }
    private static readonly TimeSpan RetryWindow = TimeSpan.FromMinutes(10);
    private static readonly int SessionId = GetSessionId();
    private const string HealthStandProfile = "LOPATA_UPDATE_STAND_ROOT";

    internal static void Run(RecoveryPaths paths, string request)
    {
        using var monitor = Acquire(paths);
        if (monitor is null) return;
        bool background = request == "launch-background";
        var mode = LaunchMode.Normal;
        RecoveryCheckpoint? beforeRestart = null;
        ExitSnapshot? last;
        if (request is "launch" or "launch-background")
        {
            var existing = FindExistingSession(paths);
            last = existing is null ? LaunchAndWait(paths, background, mode) : WaitForExit(paths, existing);
        }
        else if (ParseWatch(request) is { } watching
            && RecoveryPaths.Read<SessionRecord>(paths.SessionFile(watching)) is not null)
            last = WaitForExit(paths, watching);
        else
            return;

        while (last is not null)
        {
            var record = last.Session;
            background = record?.Background ?? background;
            if (record is { Intent: not RecoveryExitIntent.Active })
            {
                paths.Log(record.Clean && last.ExitCode is null or 0 ? "expected_exit" : "interrupted_exit",
                    last.Identity, new { reason = record.Intent.ToString(), last.ExitCode });
                if (FindExistingSession(paths) is not { } successor) return;
                mode = LaunchMode.Normal;
                last = WaitForExit(paths, successor);
                continue;
            }

            paths.Log("unexpected_exit", last.Identity, last.ExitCode);
            if (record?.SafeRecovery == true || mode == LaunchMode.Safe)
            {
                paths.Log("restart_limit", last.Identity);
                return;
            }
            if (FindExistingSession(paths) is { } existing)
            {
                paths.Log("already_running", last.Identity);
                mode = LaunchMode.Normal;
                last = WaitForExit(paths, existing);
                continue;
            }
            bool afterRecovery = record?.CrashRecovery == true || mode == LaunchMode.Recovery;
            var uptime = DateTime.UtcNow - new DateTime(last.Identity.StartTicks, DateTimeKind.Utc);
            var checkpoint = RecoveryCheckpoint.Capture(paths.StateRoot);
            bool sameCheckpoint = checkpoint is not null && checkpoint == beforeRestart;
            mode = afterRecovery && (uptime < RetryWindow || sameCheckpoint) ? LaunchMode.Safe : LaunchMode.Recovery;
            beforeRestart = checkpoint;
            RecoveryNotification.BeforeRestart(paths, last.Identity, safeMode: mode == LaunchMode.Safe);
            last = LaunchAndWait(paths, background && mode != LaunchMode.Safe, mode);
        }
    }

    private static FileStream? Acquire(RecoveryPaths paths)
    {
        Directory.CreateDirectory(paths.DirectoryPath);
        var file = Path.Combine(paths.DirectoryPath, $"monitor-{SessionId}.lock");
        var time = Stopwatch.StartNew();
        while (true)
        {
            try { return new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) when (error.HResult == unchecked((int)0x80070020))
            {
                if (time.Elapsed >= TimeSpan.FromSeconds(2)) return null;
                Thread.Sleep(100);
            }
        }
    }

    private static ExitSnapshot? LaunchAndWait(RecoveryPaths paths, bool background, LaunchMode mode)
    {
        var token = Guid.NewGuid();
        var registration = paths.RegistrationFile(token);
        var redirect = paths.RedirectFile(token);
        var handoff = paths.UpdateHandoffFile(token);
        var info = new ProcessStartInfo(paths.Application) { UseShellExecute = false, WorkingDirectory = paths.Installation };
        info.Environment[RecoveryPaths.LaunchTokenVariable] = token.ToString("N");
        if (!string.Equals(paths.StateRoot, RecoveryPaths.DefaultStateRoot, StringComparison.OrdinalIgnoreCase))
        {
            info.Environment[HealthStandProfile] = paths.StateRoot;
            info.Environment["LOPATA_RECOVERY_TEST"] = "1";
        }
        if (background) info.ArgumentList.Add("--background");
        if (mode == LaunchMode.Recovery) info.ArgumentList.Add("--recover-session");
        if (mode == LaunchMode.Safe) info.ArgumentList.Add("--recover-safe");
        try
        {
            using var process = Process.Start(info) ?? throw new IOException("Could not launch LOPATA.");
            var identity = IdentityOf(process);
            paths.Log(mode switch { LaunchMode.Recovery => "restart", LaunchMode.Safe => "restart_safe", _ => "launch" }, identity);
            var snapshot = WaitForExit(paths, identity, process);
            if (!File.Exists(redirect)) return snapshot;

            // An installed launch may pass through the existing transactional updater.
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromMinutes(2))
            {
                if (File.Exists(handoff))
                {
                    paths.Log("update_handoff", identity);
                    return null;
                }
                if (RecoveryPaths.Read<ProcessIdentity>(registration) is { } adopted && adopted != identity)
                {
                    paths.Log("redirect_completed", adopted);
                    return WaitForExit(paths, adopted);
                }
                Thread.Sleep(200);
            }
            paths.Log("redirect_timed_out", identity);
            return null;
        }
        finally
        {
            RecoveryPaths.Delete(registration);
            RecoveryPaths.Delete(redirect);
            RecoveryPaths.Delete(handoff);
        }
    }

    private static ExitSnapshot WaitForExit(RecoveryPaths paths, ProcessIdentity identity, Process? launched = null)
    {
        using var attached = launched is null ? Open(paths, identity) : null;
        var process = launched ?? attached;
        int? exitCode = null;
        if (process is not null)
        {
            paths.Log("watching", identity);
            process.WaitForExit();
            exitCode = process.ExitCode;
        }
        var file = paths.SessionFile(identity);
        var record = RecoveryPaths.Read<SessionRecord>(file);
        RecoveryPaths.Delete(file);
        return new ExitSnapshot(identity, exitCode, record);
    }

    private static ProcessIdentity? FindExistingSession(RecoveryPaths paths)
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(paths.Application)))
        {
            using (process)
            {
                try
                {
                    if (!IsApplication(paths, process)) continue;
                    var identity = IdentityOf(process);
                    if (RecoveryPaths.Read<SessionRecord>(paths.SessionFile(identity)) is { Intent: RecoveryExitIntent.Active })
                        return identity;
                }
                catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
            }
        }
        return null;
    }

    private static Process? Open(RecoveryPaths paths, ProcessIdentity identity)
    {
        Process? candidate = null;
        try
        {
            candidate = Process.GetProcessById(identity.Id);
            if (IsApplication(paths, candidate) && IdentityOf(candidate) == identity)
                return candidate;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception) { }
        candidate?.Dispose();
        return null;
    }

    private static bool IsApplication(RecoveryPaths paths, Process process) =>
        process.SessionId == SessionId &&
        string.Equals(process.MainModule?.FileName, paths.Application, StringComparison.OrdinalIgnoreCase);

    private static int GetSessionId()
    {
        using var current = Process.GetCurrentProcess();
        return current.SessionId;
    }

    private static ProcessIdentity IdentityOf(Process process) =>
        new(process.Id, process.StartTime.ToUniversalTime().Ticks);

    private static ProcessIdentity? ParseWatch(string request)
    {
        if (!request.StartsWith("watch:", StringComparison.Ordinal)) return null;
        var fields = request.AsSpan(6);
        var separator = fields.IndexOf('-');
        if (separator < 0
            || !int.TryParse(fields[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out int pid)
            || !long.TryParse(fields[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks)
            || pid <= 0 || ticks <= 0 || ticks > DateTime.MaxValue.Ticks) return null;
        return new ProcessIdentity(pid, ticks);
    }

    private sealed record ExitSnapshot(ProcessIdentity Identity, int? ExitCode, SessionRecord? Session);
}
