using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Lopata.Updates;
using AIHub.Models;

namespace AIHub.Services;

internal static class ApplicationUpdateStartup
{
    public static string? HealthTransactionId { get; private set; }
    private static CoreVoiceRollbackStore? _voiceRecovery;

    public static void PrepareVoiceSettingsRecovery()
    {
        if (HealthTransactionId is not { } id) return;
        try
        {
            var state = InstalledUpdateState.Read();
            if (state is null || !Path.TrimEndingDirectorySeparator(state.AppDirectory).Equals(
                Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase)) return;
            var keys = UpdateReleaseKeys.Trusted;
            var journal = new UpdateTransaction(state.Roots(), keys, state.StateDirectory).ReadJournal();
            if (journal is not { Phase: UpdatePhase.AwaitingHealth } || journal.Id != id) return;
            _voiceRecovery = new(state.StateDirectory, new(AppDataPaths.BaseDirectory));

            // A rollback runs outside the installed application. Keep the verified host there
            // so the notification survives restoring an older application and its managed worker.
            var host = journal.Target.Verify(keys).Files.SingleOrDefault(file =>
                file.Root == "app" && file.Path == "Updater/LOPATA.Updater.exe");
            if (host is not null) RefreshRecoveryHost(state.Roots().Resolve(host), host);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
            or System.Text.Json.JsonException or CryptographicException)
        {
            OwnedProcessRegistry.Log("voice_settings_recovery_setup_failed", "update", detail: error.Message);
        }
    }

    public static void RecordVoiceSettingsMigration(CoreVoiceSelection before)
    {
        if (HealthTransactionId is { } id) _voiceRecovery?.Capture(id, before);
    }

    private static void RefreshRecoveryHost(string source, UpdateFile expected)
    {
        var path = InstalledUpdateState.LauncherPath;
        SafeUpdatePath.RejectLinks(path);
        if (File.Exists(path) && Matches(path, expected)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var input = File.OpenRead(source))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }
            if (!Matches(temporary, expected)) throw new InvalidDataException("Recovery host copy failed verification.");
            SafeUpdatePath.RejectLinks(path);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }

        static bool Matches(string file, UpdateFile expected)
        {
            using var stream = File.OpenRead(file);
            return stream.Length == expected.Size && Convert.ToHexStringLower(SHA256.HashData(stream)) == expected.Sha256;
        }
    }

    public static bool RedirectInstalledLaunch(string[] args)
    {
        if (args.Contains("--launched-by-updater", StringComparer.Ordinal))
        {
            var index = Array.IndexOf(args, "--update-health");
            if (index >= 0 && index + 1 < args.Length && Guid.TryParseExact(args[index + 1], "N", out _))
                HealthTransactionId = args[index + 1];
            return false;
        }
        if (AppDataPaths.ProjectRoot is not null) return false;
        var registration = InstalledUpdateState.Read();
        if (registration is null) return false; // Legacy installations use their existing launch path until migration.
        if (!Path.GetFullPath(registration.AppDirectory).TrimEnd(Path.DirectorySeparatorChar)
            .Equals(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return false;
        // Older installed launchers do not forward file arguments, and a prepared full installer
        // cannot preserve this request. The verified installed application accepts it directly.
        // Ordinary launches retain the complete update/recovery route.
        if (IsImageShellLaunch(args) || AudioShellRequest.ParseArguments(args) is not null
            || IsVectorFileLaunch(args) || IsVideoFileLaunch(args)) return false;
        var start = Launcher("--launch");
        if (args.Contains("--background", StringComparer.Ordinal)) start.ArgumentList.Add("--background");
        if (args.Contains("--recover-safe", StringComparer.Ordinal)) start.ArgumentList.Add("--recover-safe");
        else if (args.Contains("--recover-session", StringComparer.Ordinal)) start.ArgumentList.Add("--recover-session");
        AddWaitForThisProcess(start);
        _ = Process.Start(start) ?? throw new IOException("Could not start the update launcher.");
        return true;
    }

    internal static bool IsImageShellLaunch(string[] args) => ImageShellRequest.ParseArguments(args) is not null;
    internal static bool IsVectorFileLaunch(string[] args) => VectorEditor.VectorFileRequest.Parse(args) is not null;
    internal static bool IsVideoFileLaunch(string[] args) => VideoEditor.VideoFileRequest.Parse(args) is not null;

    public static ProcessStartInfo Launcher(string command)
    {
        SafeUpdatePath.RejectLinks(InstalledUpdateState.LauncherPath);
        if (!File.Exists(InstalledUpdateState.LauncherPath)) throw new IOException("The update launcher is missing; reinstall LOPATA.");
        var start = new ProcessStartInfo(InstalledUpdateState.LauncherPath) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(command);
        return start;
    }

    public static void AddWaitForThisProcess(ProcessStartInfo start)
    {
        using var process = Process.GetCurrentProcess();
        start.ArgumentList.Add("--wait"); start.ArgumentList.Add(process.Id.ToString());
        start.ArgumentList.Add(process.StartTime.ToUniversalTime().Ticks.ToString());
    }

    public static async Task ConfirmHealthyAsync(CancellationToken token)
    {
        if (HealthTransactionId is not { } id) return;
        var start = Launcher("--confirm"); start.ArgumentList.Add(id);
        using var process = Process.Start(start) ?? throw new IOException("Could not confirm update health.");
        await process.WaitForExitAsync(token);
        if (process.ExitCode != 0) throw new IOException("Update health verification failed.");
        HealthTransactionId = null;
    }
}
