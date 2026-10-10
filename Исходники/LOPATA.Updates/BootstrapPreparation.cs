namespace Lopata.Updates;

/// <summary>The native starter downloads only signed engine/license packages. Application
/// installation remains the responsibility of the verified, self-contained update host.</summary>
public sealed class BootstrapPreparation(HttpClient http, IReadOnlyDictionary<string, string> keys)
{
    public const string ProtocolFile = "Installer/bootstrap-v1.txt";
    public int MaximumParallelConnections { get; set; }

    public static UpdateFile[] RequiredFiles(UpdateManifest target)
    {
        target.Validate();
        var paths = new[] { "Updater/LOPATA.Updater.exe", "Licenses/installer.txt",
            "Licenses/installer-receipt.json", ProtocolFile };
        return paths.Select(path => target.Files.SingleOrDefault(f => f.Root == "app" && f.Path == path)
            ?? throw new InvalidDataException("This release does not support the universal installer: " + path)).ToArray();
    }

    public async Task<string> PrepareAsync(SignedManifest signed, string cache, string stage,
        IProgress<UpdateTransferProgress>? progress = null, CancellationToken token = default)
    {
        var target = signed.Verify(keys);
        var required = RequiredFiles(target);
        var ids = required.Select(f => f.Package).ToHashSet(StringComparer.Ordinal);
        var packages = target.Packages.Where(p => ids.Contains(p.Id)).ToArray();
        var downloader = new UpdatePackageDownloader(http, cache)
            { MaximumParallelConnections = MaximumParallelConnections };
        var archives = await downloader.DownloadManyAsync(target, packages, progress, token);
        foreach (var package in packages)
        {
            progress?.Report(new(package.Id, package.Size, package.Size, "extracting"));
            await UpdatePackageExtractor.ExtractAsync(archives[package.Id], package, target, stage, token);
        }
        foreach (var file in required)
            if (!await UpdatePlanner.MatchesAsync(SafeUpdatePath.Resolve(stage, file.Key), file, token))
                throw new InvalidDataException("Bootstrap file verification failed: " + file.Key);
        if ((await File.ReadAllTextAsync(SafeUpdatePath.Resolve(stage, "app/" + ProtocolFile), token)).Trim() != "1")
            throw new InvalidDataException("Unsupported bootstrap protocol.");
        return SafeUpdatePath.Resolve(stage, "app/Updater/LOPATA.Updater.exe");
    }
}
