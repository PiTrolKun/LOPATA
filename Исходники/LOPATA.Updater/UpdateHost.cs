using System.Diagnostics;
using System.Text.Json;
using Lopata.Updates;

namespace Lopata.Updater;

internal sealed class UpdateHost(Action<string> status)
{
    private static readonly IReadOnlyDictionary<string, string> Keys = UpdateReleaseKeys.Trusted;

    public async Task RunAsync(string[] args, CancellationToken token = default)
    {
        var worker = args.Contains("--worker", StringComparer.Ordinal);
        args = args.Where(a => a != "--worker").ToArray();
        var mode = args.FirstOrDefault() ?? "--launch";
        if (mode is "--register" or "--install" or "--setup")
        {
            if (args.Length != (mode == "--setup" ? 6 : 5)) throw new ArgumentException("Registration requires application, two backend roots, and signed manifest.");
            var installation = new InstalledUpdateState(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]));
            var signed = SignedManifest.Read(await File.ReadAllBytesAsync(args[4]));
            if (mode is "--install" or "--setup")
            {
                EnsureNotRunning(installation);
                if (mode == "--setup" && InstalledUpdateState.Read() is { } registered &&
                    (!SameRoot(registered.AppDirectory, installation.AppDirectory) ||
                     !SameRoot(registered.LlamaDirectory, installation.LlamaDirectory) ||
                     !SameRoot(registered.ChatLlmDirectory, installation.ChatLlmDirectory)))
                    throw new IOException("Choose the registered installation directory before updating LOPATA.");
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
                var total = signed.Verify(Keys).Packages.Sum(p => p.Size);
                var transferWatch = Stopwatch.StartNew();
                var completed = new Dictionary<string, long>(StringComparer.Ordinal);
                var received = new Dictionary<string, long>(StringComparer.Ordinal);
                long networkBytes = 0;
                var progress = new Progress<UpdateTransferProgress>(p =>
                {
                    lock (completed)
                    {
                        completed[p.Package] = Math.Max(completed.GetValueOrDefault(p.Package), p.StoredBytes);
                        if (p.Stage == "downloading")
                        {
                            if (received.TryGetValue(p.Package, out var before)) networkBytes += Math.Max(0, p.StoredBytes - before);
                            received[p.Package] = p.StoredBytes;
                        }
                        status(HostText.Get("UpdateHost.Downloading") + " " + (completed.Values.Sum() / 1048576d).ToString("F1")
                            + " / " + (total / 1048576d).ToString("F1") + " MB; "
                            + (networkBytes / 1048576d / Math.Max(transferWatch.Elapsed.TotalSeconds, .1)).ToString("F1") + " MB/s; " + p.Stage);
                    }
                });
                if (mode == "--setup")
                    await new SetupInstallation(http, Keys) { MaximumParallelConnections = UpdateDownloadSettings.Normalize(int.Parse(args[5])) }
                        .InstallAsync(installation.Roots(), installation.StateDirectory, installation.CacheDirectory,
                            installation.StageDirectory(signed.Verify(Keys).Version), signed, progress, token,
                            plannedBytes: bytes => total = bytes);
                else
                    await new FreshInstallation(http, Keys) { MaximumParallelConnections = DownloadConnections() }
                        .InstallAsync(installation.Roots(), installation.StateDirectory,
                        installation.CacheDirectory, installation.StageDirectory(signed.Verify(Keys).Version), signed, progress, token);
            }
            await new UpdateTransaction(installation.Roots(), Keys, installation.StateDirectory).RegisterInstalledAsync(signed, token);
            installation.Save();
            new PreparedUpdateStore(installation.StateDirectory, Keys).Clear();
            return;
        }
        var state = InstalledUpdateState.Read() ?? throw new IOException("The transition installer has not registered this installation.");
        if (mode == "--configure-autostart")
        {
            if (args.Length != 2 || args[1] is not ("0" or "1")) throw new ArgumentException("Expected startup choice 0 or 1.");
            StartupInstallation.Configure(args[1] == "1"); return;
        }
        var transaction = new UpdateTransaction(state.Roots(), Keys, state.StateDirectory);
        if (!worker && mode != "--confirm" && await WorkerDispatch.TryStartAsync(state, transaction, args,
                waitForExit: mode is "--unregister" or "--recover")) return;
        await WaitForCallerAsync(args);
        if (mode == "--confirm")
        {
            if (args.Length != 2) throw new ArgumentException("Health confirmation requires a transaction id.");
            await transaction.ConfirmHealthyAsync(args[1]);
            new PreparedUpdateStore(state.StateDirectory, Keys).Clear();
            return;
        }
        if (mode is not ("--launch" or "--apply" or "--unregister" or "--recover")) throw new ArgumentException("Unknown updater command.");
        if (mode == "--recover") EnsureNotRunning(state);
        var pendingStore = new PreparedUpdateStore(state.StateDirectory, Keys);
        var journal = transaction.ReadJournal();
        if (journal is { Phase: not (UpdatePhase.Healthy or UpdatePhase.RolledBack) })
        {
            EnsureNotRunning(state);
            status(HostText.Get("UpdateHost.Recovering"));
            await transaction.RecoverAsync();
            // Do not automatically repeat a failed update on every launch.
            if (pendingStore.Read() is { } interrupted) pendingStore.Save(interrupted with { ApplyOnNextLaunch = false });
        }
        if (mode == "--unregister")
        {
            var startup = new StartupRegistration(InstalledUpdateState.LauncherPath);
            if (startup.IsRegistered) startup.SetEnabled(false);
            await RemoveManagedFilesAsync(state, transaction);
            return;
        }
        if (mode == "--recover") return;
        var pending = pendingStore.Read();
        if (pending is not null && (mode == "--apply" || pending.ApplyOnNextLaunch))
        {
            EnsureNotRunning(state);
            if (pending.Delivery == UpdateDelivery.FullInstaller)
            {
                if (!await UpdatePlanner.MatchesAsync(pending.InstallerPath!, pending.Installer!.Size, pending.Installer.Sha256))
                    throw new InvalidDataException("Prepared installer failed verification.");
                // Starting the installer consumes this launch decision; cancellation must not cause a startup loop.
                pendingStore.Save(pending with { ApplyOnNextLaunch = false });
                var setup = new ProcessStartInfo(pending.InstallerPath!) { UseShellExecute = true };
                setup.ArgumentList.Add("/DIR=" + state.AppDirectory);
                setup.ArgumentList.Add("/WAITFORPID=" + Environment.ProcessId);
                _ = Process.Start(setup) ?? throw new IOException("Could not start the full installer.");
                return;
            }
            status(HostText.Get("UpdateHost.Verifying"));
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            await new FileUpdatePreparation(http, Keys) { MaximumParallelConnections = DownloadConnections() }
                .StageAsync(state, pending.Files!, token: token);
            var previous = SignedManifest.Read(await File.ReadAllBytesAsync(transaction.InstalledManifestPath));
            var id = await transaction.PrepareAsync(previous, pending.Files!, state.StageDirectory(pending.Version), token);
            try
            {
                status(HostText.Get("UpdateHost.Applying"));
                await transaction.ApplyAsync(id, p => status(HostText.Get("UpdateHost.Applying") + " " + p.CompletedFiles), token);
            }
            catch
            {
                status(HostText.Get("UpdateHost.Recovering"));
                await transaction.RecoverAsync();
                pendingStore.Save(pending with { ApplyOnNextLaunch = false });
                throw;
            }
            pendingStore.Save(pending with { ApplyOnNextLaunch = false });
            StartApplication(state, id, args.Contains("--background", StringComparer.Ordinal));
            return;
        }
        if (mode == "--apply") throw new IOException("No prepared update is available.");
        StartApplication(state, null, args.Contains("--background", StringComparer.Ordinal));
    }

    private static int DownloadConnections() => UpdateDownloadSettings.Read(Path.Combine(InstalledUpdateState.UserDataDirectory, "settings.json"));
    private static bool SameRoot(string left, string right) => string.Equals(Path.GetFullPath(left).TrimEnd('\\'),
        Path.GetFullPath(right).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static async Task WaitForCallerAsync(string[] args)
    {
        var index = Array.IndexOf(args, "--wait");
        if (index < 0) return;
        if (index + 2 >= args.Length || !int.TryParse(args[index + 1], out var pid) || !long.TryParse(args[index + 2], out var ticks))
            throw new ArgumentException("Invalid caller identity.");
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.StartTime.ToUniversalTime().Ticks != ticks) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (ArgumentException) { }
    }

    private static void EnsureNotRunning(InstalledUpdateState state)
    {
        foreach (var process in Process.GetProcessesByName("AIHub"))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, Path.Combine(state.AppDirectory, "AIHub.exe"), StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Close the running LOPATA installation before updating it.");
                }
                catch (System.ComponentModel.Win32Exception) { }
                catch (InvalidOperationException) { }
            }
        }
    }

    private static void StartApplication(InstalledUpdateState state, string? transactionId, bool background)
    {
        var path = SafeUpdatePath.Resolve(state.AppDirectory, "AIHub.exe");
        var start = new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = state.AppDirectory };
        start.ArgumentList.Add("--launched-by-updater");
        if (background) start.ArgumentList.Add("--background");
        if (transactionId is not null) { start.ArgumentList.Add("--update-health"); start.ArgumentList.Add(transactionId); }
        _ = Process.Start(start) ?? throw new IOException("Could not launch LOPATA.");
    }

    private static async Task RemoveManagedFilesAsync(InstalledUpdateState state, UpdateTransaction transaction)
    {
        EnsureNotRunning(state);
        await transaction.RemoveManagedFilesAsync();
        new PreparedUpdateStore(state.StateDirectory, Keys).Clear();
        SafeUpdatePath.RejectLinks(InstalledUpdateState.RegistrationPath);
        File.Delete(InstalledUpdateState.RegistrationPath);
    }
}
