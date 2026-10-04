using System.Diagnostics;
using System.IO;
using Lopata.Updates;
using AIHub.Models;

namespace AIHub.Services;

internal static class ApplicationUpdateStartup
{
    public static string? HealthTransactionId { get; private set; }

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
        if (IsImageShellLaunch(args)) return false;
        var start = Launcher("--launch");
        if (args.Contains("--background", StringComparer.Ordinal)) start.ArgumentList.Add("--background");
        AddWaitForThisProcess(start);
        _ = Process.Start(start) ?? throw new IOException("Could not start the update launcher.");
        return true;
    }

    internal static bool IsImageShellLaunch(string[] args) => ImageShellRequest.ParseArguments(args) is not null;

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
