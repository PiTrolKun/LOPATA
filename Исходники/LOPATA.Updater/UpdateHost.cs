using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Lopata.Inspector;
using Lopata.Recovery;
using Lopata.Updates;

namespace Lopata.Updater;

internal sealed class UpdateHost(Action<string> status,
    Action<CoreVoiceRollbackStore, string>? reportSettingsRollback = null)
{
    private static readonly IReadOnlyDictionary<string, string> Keys = UpdateReleaseKeys.Trusted;
    private string? _inspectorResumePath;

    public async Task RunAsync(string[] args, CancellationToken token = default)
    {
        try { await RunCoreAsync(args, token); }
        finally
        {
            if (_inspectorResumePath is { } application)
            {
                _inspectorResumePath = null;
                ResumeInspector(application);
            }
        }
    }

    private async Task RunCoreAsync(string[] args, CancellationToken token)
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
                if (mode == "--setup" && InstalledUpdateState.Read() is { } registered &&
                    (!SameRoot(registered.AppDirectory, installation.AppDirectory) ||
                     !SameRoot(registered.LlamaDirectory, installation.LlamaDirectory) ||
                     !SameRoot(registered.ChatLlmDirectory, installation.ChatLlmDirectory)))
                    throw new IOException("Choose the registered installation directory before updating LOPATA.");
                await WaitForInstallationMutationAsync(installation, token);
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
            status(HostText.Get("UpdateHost.InitializingCollection"));
            await CollectionInstallation.InitializeAsync(installation, signed, Keys, HostText.Get, token);
            ResumeInspector(Path.Combine(installation.AppDirectory, "AIHub.exe"));
            _inspectorResumePath = null;
            return;
        }
        var state = InstalledUpdateState.Read() ?? throw new IOException("The transition installer has not registered this installation.");
        if (mode == "--configure-autostart")
        {
            if (args.Length != 2 || args[1] is not ("0" or "1")) throw new ArgumentException("Expected startup choice 0 or 1.");
            StartupInstallation.Configure(args[1] == "1"); return;
        }
        var transaction = new UpdateTransaction(state.Roots(), Keys, state.StateDirectory);
        var journal = transaction.ReadJournal();
        var voiceRecovery = new CoreVoiceRollbackStore(state.StateDirectory, new(InstalledUpdateState.UserDataDirectory));
        var noticePending = journal is { Phase: UpdatePhase.RolledBack } && !voiceRecovery.WasReported(journal.Id);
        if (!worker && mode is not ("--confirm" or "--rollback-to") && !noticePending
            && await WorkerDispatch.TryStartAsync(state, transaction, args, waitForExit: mode is "--unregister" or "--recover")) return;
        await WaitForCallerAsync(args);
        if (mode == "--confirm")
        {
            if (args.Length != 2) throw new ArgumentException("Health confirmation requires a transaction id.");
            await transaction.ConfirmHealthyAsync(args[1]);
            new PreparedUpdateStore(state.StateDirectory, Keys).Clear();
            return;
        }
        if (mode == "--rollback-to")
        {
            if (args.Length < 2) throw new ArgumentException("Rollback requires a version.");
            var prepared = new PreparedUpdateStore(state.StateDirectory, Keys);
            if (prepared.Read() is not null) throw new IOException("Cancel the prepared update before rolling back.");
            await WaitForInstallationMutationAsync(state, token);
            status(HostText.Get("UpdateHost.Recovering"));
            if (!transaction.RollbackVersions().Contains(args[1])) throw new IOException("The selected rollback point is no longer available.");
            if (UpdateManifest.NumericVersion(args[1]) < Version.Parse(LegacyWorkspaceMoves.FirstSupportedVersion))
                await LegacyWorkspaceMoves.RestoreAsync(InstalledUpdateState.UserDataDirectory, token);
            await transaction.RollbackToAsync(args[1], token);
            StartApplication(state, null, false);
            return;
        }
        if (mode is not ("--launch" or "--apply" or "--unregister" or "--recover")) throw new ArgumentException("Unknown updater command.");
        var pendingStore = new PreparedUpdateStore(state.StateDirectory, Keys);
        journal = transaction.ReadJournal();
        var rollback = journal is { Phase: not (UpdatePhase.Healthy or UpdatePhase.RolledBack) };
        if (rollback || mode is "--recover" or "--unregister")
            await WaitForInstallationMutationAsync(state, token);
        if (rollback)
        {
            status(HostText.Get("UpdateHost.Recovering"));
            await transaction.RecoverAsync();
            // Do not automatically repeat a failed update on every launch.
            if (pendingStore.Read() is { } interrupted) pendingStore.Save(interrupted with { ApplyOnNextLaunch = false });
            journal = transaction.ReadJournal();
        }
        if (mode == "--unregister")
        {
            var collection = new Lopata.Collection.CollectionShellRegistration(Path.Combine(state.AppDirectory, "AIHub.exe"));
            if (collection.CanManageRegistration && collection.HasOwnedRegistration)
            {
                var directory = Lopata.Collection.CollectionPaths.DefaultDirectory;
                try
                {
                    var error = Lopata.Collection.CollectionDeactivation.Disable(directory);
                    if (error is not null)
                        try
                        {
                            var log = Path.Combine(state.StateDirectory, "collection-unregister.json");
                            SafeUpdatePath.RejectLinks(log);
                            Lopata.Collection.CollectionFiles.WriteAtomic(log, JsonSerializer.SerializeToUtf8Bytes(new { error }));
                        }
                        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { }
                }
                finally { collection.Disable(); }
            }
            var startup = new StartupRegistration(InstalledUpdateState.LauncherPath);
            if (startup.IsRegistered) startup.SetEnabled(false);
            var application = Path.Combine(state.AppDirectory, "AIHub.exe");
            if (File.Exists(Path.Combine(state.AppDirectory, InspectorStartup.ExecutableName)))
                InspectorStartup.RemoveAutomaticStart(InstalledUpdateState.UserDataDirectory, application);
            await RemoveManagedFilesAsync(state, transaction);
            _inspectorResumePath = null;
            RecoveryTask.TryUnregister(application, InstalledUpdateState.UserDataDirectory);
            return;
        }
        if (mode == "--recover")
        {
            // Full installer preflight. The newly registered installation resumes the inspector.
            _inspectorResumePath = null;
            return;
        }
        if (journal is { Phase: UpdatePhase.RolledBack } && reportSettingsRollback is not null)
        {
            using var reportLease = voiceRecovery.TryBeginReport(journal.Id);
            if (reportLease is null) return; // The other launcher will start the restored application.
            if (!voiceRecovery.WasReported(journal.Id))
            {
                reportSettingsRollback(voiceRecovery, journal.Id);
                voiceRecovery.MarkReported(journal.Id);
            }
        }
        var recoveryArgument = args.Contains("--recover-safe", StringComparer.Ordinal) ? "--recover-safe"
            : args.Contains("--recover-session", StringComparer.Ordinal) ? "--recover-session" : null;
        var pending = pendingStore.Read();
        if (pending is not null && (mode == "--apply" || recoveryArgument is null && pending.ApplyOnNextLaunch))
        {
            await WaitForInstallationMutationAsync(state, token, suspendInspector: pending.Delivery != UpdateDelivery.FullInstaller);
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
            await WaitForInstallationMutationAsync(state, token);
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
        StartApplication(state, null, args.Contains("--background", StringComparer.Ordinal), recoveryArgument);
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

    private async Task WaitForInstallationMutationAsync(InstalledUpdateState state, CancellationToken token, bool suspendInspector = true)
    {
        EnsureNotRunning(state);
        var application = Path.Combine(state.AppDirectory, "AIHub.exe");
        if (suspendInspector && File.Exists(Path.Combine(state.AppDirectory, InspectorStartup.ExecutableName)))
        {
            _inspectorResumePath = application;
            await InspectorStartup.SuspendForServicingAsync(InstalledUpdateState.UserDataDirectory, application, token);
        }
        RecoverySession.RequestUpdateHandoff(application, InstalledUpdateState.UserDataDirectory);
        var executable = Path.Combine(state.AppDirectory, "LOPATA.Recovery.exe");
        var candidates = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            foreach (var process in candidates)
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
                        await process.WaitForExitAsync(timeout.Token);
                }
                catch (System.ComponentModel.Win32Exception) { }
                catch (InvalidOperationException) { }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new IOException("The LOPATA recovery process did not release the installation files.");
        }
        finally
        {
            foreach (var process in candidates) process.Dispose();
        }
        EnsureNotRunning(state);
    }

    private static void ResumeInspector(string application)
    {
        try { InspectorStartup.Reconcile(InstalledUpdateState.UserDataDirectory, application); }
        catch (Exception error) when (error is IOException or COMException or UnauthorizedAccessException
            or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Trace.TraceWarning("Could not restore the inspector startup registration: {0}", error.Message);
        }
    }

    private static void StartApplication(InstalledUpdateState state, string? transactionId, bool background, string? recoveryArgument = null)
    {
        var path = SafeUpdatePath.Resolve(state.AppDirectory, "AIHub.exe");
        var start = new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = state.AppDirectory };
        start.ArgumentList.Add("--launched-by-updater");
        if (background) start.ArgumentList.Add("--background");
        if (recoveryArgument is not null) start.ArgumentList.Add(recoveryArgument);
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
