namespace Lopata.Updates;

/// <summary>Network installation uses the same signed packages and transaction as file updates.</summary>
public sealed class FreshInstallation(HttpClient http, IReadOnlyDictionary<string, string> keys)
{
    public int MaximumParallelConnections { get; set; }
    public async Task InstallAsync(UpdateRoots roots, string stateDirectory, string cacheDirectory, string stageDirectory,
        SignedManifest signed, IProgress<UpdateTransferProgress>? progress = null, CancellationToken token = default)
    {
        var manifest = signed.Verify(keys);
        var transaction = new UpdateTransaction(roots, keys, stateDirectory);
        if (transaction.ReadJournal() is { Phase: not (UpdatePhase.Healthy or UpdatePhase.RolledBack) })
            await transaction.RecoverAsync(token: token);
        // Upgrades from an existing installation use the full installer or in-app updater.
        if (File.Exists(transaction.InstalledManifestPath))
        {
            var installed = SignedManifest.Read(await File.ReadAllBytesAsync(transaction.InstalledManifestPath, token));
            // Resume a completed first install whose final Inno registration was interrupted.
            if (installed.Payload == signed.Payload)
            {
                foreach (var file in manifest.Files)
                    if (!await UpdatePlanner.MatchesAsync(roots.Resolve(file), file, token))
                        throw new InvalidDataException("The completed network installation changed before registration.");
                return;
            }
            throw new InvalidOperationException("Use a full installer to upgrade an existing installation.");
        }
        var plan = await UpdatePlanner.CreateAsync(null, manifest, roots, token);
        var downloader = new UpdatePackageDownloader(http, cacheDirectory)
            { MaximumParallelConnections = MaximumParallelConnections };
        var archives = await downloader.DownloadManyAsync(manifest, plan.Packages, progress, token);
        foreach (var package in plan.Packages)
        {
            var path = archives[package.Id];
            progress?.Report(new(package.Id, package.Size, package.Size, "extracting"));
            await UpdatePackageExtractor.ExtractAsync(path, package, manifest, stageDirectory, token);
        }
        var id = await transaction.PrepareAsync(null, signed, stageDirectory, token);
        try
        {
            await transaction.ApplyAsync(id, token: token);
            // Inno finalizes a first installation after full file verification; there is no prior application to restore.
            await transaction.ConfirmHealthyAsync(id, token);
        }
        catch
        {
            await transaction.RecoverAsync(token: CancellationToken.None);
            throw;
        }
    }
}
