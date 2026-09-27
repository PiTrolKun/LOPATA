using System.Diagnostics;
using Lopata.Updates;

namespace Lopata.Updater;

internal static class WorkerDispatch
{
    public static async Task<bool> TryStartAsync(InstalledUpdateState state, UpdateTransaction transaction, string[] args, bool waitForExit = false)
    {
        var journal = transaction.ReadJournal();
        // The external bootstrap remains usable when the managed installation is interrupted.
        if (journal is { Phase: UpdatePhase.Applying or UpdatePhase.RollingBack }
            || (journal is { Phase: UpdatePhase.AwaitingHealth } && args.FirstOrDefault() != "--confirm")) return false;
        if (!File.Exists(transaction.InstalledManifestPath)) return false;
        var manifest = SignedManifest.Read(await File.ReadAllBytesAsync(transaction.InstalledManifestPath)).Verify(UpdateReleaseKeys.Trusted);
        var worker = manifest.Files.SingleOrDefault(f => f.Root == "app" && f.Path == "Updater/LOPATA.Updater.exe");
        if (worker is null) return false;
        var source = state.Roots().Resolve(worker);
        if (!File.Exists(source) || !await UpdatePlanner.MatchesAsync(source, worker)) return false;
        var folder = SafeUpdatePath.Resolve(state.StateDirectory, "workers/" + worker.Sha256);
        Directory.CreateDirectory(folder);
        var path = SafeUpdatePath.Resolve(folder, "LOPATA.Updater.exe");
        if (!File.Exists(path) || !await UpdatePlanner.MatchesAsync(path, worker))
        {
            var temporary = SafeUpdatePath.Resolve(folder, Guid.NewGuid().ToString("N") + ".tmp");
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await input.CopyToAsync(output);
                output.Flush(true);
            }
            if (!await UpdatePlanner.MatchesAsync(temporary, worker)) throw new InvalidDataException("Updater copy failed verification.");
            SafeUpdatePath.RejectLinks(path);
            File.Move(temporary, path, true);
        }
        var start = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = folder, CreateNoWindow = true };
        start.ArgumentList.Add("--worker");
        foreach (var argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start the verified updater worker.");
        // Inno must not remove files or start copying a full upgrade while a worker is still active.
        if (waitForExit)
        {
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new IOException("The verified updater worker did not complete successfully.");
        }
        return true;
    }
}
