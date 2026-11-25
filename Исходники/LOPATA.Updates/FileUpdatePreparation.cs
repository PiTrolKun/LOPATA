namespace Lopata.Updates;

public sealed class FileUpdatePreparation(HttpClient http, IReadOnlyDictionary<string, string> keys)
{
    public int MaximumParallelConnections { get; set; }
    public async Task<UpdatePlan> StageAsync(InstalledUpdateState installation, SignedManifest signed,
        IProgress<UpdateTransferProgress>? progress = null, CancellationToken token = default)
    {
        var target = signed.Verify(keys);
        var transaction = new UpdateTransaction(installation.Roots(), keys, installation.StateDirectory);
        using var maintenance = transaction.Acquire();
        if (!File.Exists(transaction.InstalledManifestPath))
            throw new InvalidDataException("A transition installer is required before file updates.");
        var previous = SignedManifest.Read(await File.ReadAllBytesAsync(transaction.InstalledManifestPath, token)).Verify(keys);
        var plan = await UpdatePlanner.CreateAsync(previous, target, installation.Roots(), token);
        var workspace = installation.WorkspaceDirectory;
        var staging = SafeUpdatePath.Resolve(workspace, "staging/" + UpdateManifest.NumericVersion(target.Version));
        var downloader = new UpdatePackageDownloader(http, Path.Combine(workspace, "packages"))
            { MaximumParallelConnections = MaximumParallelConnections };
        var archives = await downloader.DownloadManyAsync(target, plan.Packages, progress, token);
        foreach (var package in plan.Packages)
        {
            var archive = archives[package.Id];
            progress?.Report(new(package.Id, package.Size, package.Size, "extracting"));
            await UpdatePackageExtractor.ExtractAsync(archive, package, target, staging, token);
        }
        foreach (var op in plan.Operations.Where(o => o.Action == UpdateAction.Write))
            if (!await UpdatePlanner.MatchesAsync(SafeUpdatePath.Resolve(staging, op.File.Key), op.Target!, token))
                throw new InvalidDataException("File preparation is incomplete.");
        return plan;
    }
}
