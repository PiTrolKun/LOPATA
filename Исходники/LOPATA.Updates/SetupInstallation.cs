namespace Lopata.Updates;

/// <summary>Explicit installer operation. Unlike normal automatic updates, setup confirms
/// the complete installed file set before the wizard registers shortcuts/uninstallation.</summary>
public sealed class SetupInstallation(HttpClient http, IReadOnlyDictionary<string, string> keys)
{
    public int MaximumParallelConnections { get; set; }

    public async Task InstallAsync(UpdateRoots roots, string stateDirectory, string cacheDirectory,
        string stagingDirectory, SignedManifest signed,
        IProgress<UpdateTransferProgress>? progress = null, CancellationToken token = default, Action<long>? plannedBytes = null)
    {
        var target = signed.Verify(keys);
        _ = BootstrapPreparation.RequiredFiles(target);
        var transaction = new UpdateTransaction(roots, keys, stateDirectory);
        if (transaction.ReadJournal() is { Phase: not (UpdatePhase.Healthy or UpdatePhase.RolledBack) })
            await transaction.RecoverAsync(token: token);
        if (!File.Exists(transaction.InstalledManifestPath))
        {
            plannedBytes?.Invoke((await UpdatePlanner.CreateAsync(null, target, roots, token)).DownloadBytes);
            await new FreshInstallation(http, keys) { MaximumParallelConnections = MaximumParallelConnections }
                .InstallAsync(roots, stateDirectory, cacheDirectory, stagingDirectory, signed, progress, token);
            return;
        }
        var previous = SignedManifest.Read(await File.ReadAllBytesAsync(transaction.InstalledManifestPath, token));
        var plan = await UpdatePlanner.CreateAsync(previous.Verify(keys), target, roots, token);
        plannedBytes?.Invoke(plan.DownloadBytes);
        var archives = await new UpdatePackageDownloader(http, cacheDirectory)
            { MaximumParallelConnections = MaximumParallelConnections }.DownloadManyAsync(target, plan.Packages, progress, token);
        foreach (var package in plan.Packages)
            await UpdatePackageExtractor.ExtractAsync(archives[package.Id], package, target, stagingDirectory, token);
        var id = await transaction.PrepareAsync(previous, signed, stagingDirectory, token);
        try
        {
            await transaction.ApplyAsync(id, token: token);
            await transaction.ConfirmHealthyAsync(id, token);
        }
        catch
        {
            // Cancellation must not interrupt restoration of the previous registered version.
            await transaction.RecoverAsync();
            throw;
        }
    }
}
