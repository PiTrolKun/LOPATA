using System.IO;
using System.Net.Http;
using System.Text.Json;
using AIHub.Models;
using Lopata.Updates;

namespace AIHub.Services;

public sealed partial class EngineSetService
{
    public int MaximumParallelConnections { get; set; }
    public async Task DownloadAsync(string id, IProgress<ManagedModelDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        string? staging = null;
        string? stagingLeaf = null;
        try
        {
            var set = Find(id);
            var installed = await VerifyAsync(set, ct);
            ct.ThrowIfCancellationRequested();
            if (installed) return;
            var manifest = set.VerifiedManifest;
            var cache = SafeUpdatePath.Resolve(_cacheRoot, id);
            Directory.CreateDirectory(cache);
            stagingLeaf = ".prepare-" + id + "-" + Guid.NewGuid().ToString("N");
            var ownedStage = SafeUpdatePath.Resolve(_installedRoot, stagingLeaf);
            var target = SafeUpdatePath.Resolve(_installedRoot, id);
            if (Directory.Exists(ownedStage) || File.Exists(ownedStage))
                throw new IOException("The unique engine preparation path already exists.");
            Directory.CreateDirectory(ownedStage);
            staging = ownedStage;
            var reusable = await ReusablePackagesAsync(set, ct);
            var packages = manifest.Packages.Where(p => !reusable.ContainsKey(p.Id)).ToArray();
            var downloadBytes = packages.Sum(p => p.Size);
            var needed = checked(set.InstalledBytes + downloadBytes * 2 + 64L * 1024 * 1024);
            if (new DriveInfo(Path.GetPathRoot(staging)!).AvailableFreeSpace < needed)
                throw new IOException("Insufficient disk space for an engine set.");
            var downloader = new UpdatePackageDownloader(_http, cache) { MaximumParallelConnections = MaximumParallelConnections };
            var batch = new UpdateBatchProgress(packages);
            var transfer = new InlineProgress<UpdateTransferProgress>(p =>
            {
                var current = batch.Accept(p);
                progress?.Report(new("engine-set-" + id, p.Package, current.Bytes, downloadBytes, 0, current.Stage));
            });
            foreach (var files in reusable.Values)
                foreach (var (file, source) in files)
                {
                    var destination = SafeUpdatePath.Resolve(staging, file.Key);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    await using var input = File.OpenRead(source);
                    await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await input.CopyToAsync(output, ct); output.Flush(true);
                }
            var archives = await downloader.DownloadManyAsync(manifest, packages, transfer, ct);
            foreach (var package in packages)
                await UpdatePackageExtractor.ExtractAsync(archives[package.Id], package, manifest, staging, ct);
            var prepared = SafeUpdatePath.Resolve(staging, "app");
            foreach (var file in manifest.Files)
                if (!await UpdatePlanner.MatchesAsync(SafeUpdatePath.Resolve(prepared, file.Path), file, ct))
                    throw new InvalidDataException("Incomplete engine set extraction.");
            ct.ThrowIfCancellationRequested();
            SafeUpdatePath.RejectLinks(target);
            string? previous = null;
            if (Directory.Exists(target))
            {
                previous = SafeUpdatePath.Resolve(_installedRoot, ".damaged-" + id + "-" + Guid.NewGuid().ToString("N"));
                Directory.Move(target, previous);
            }
            try { Directory.Move(prepared, target); }
            catch
            {
                if (previous is not null && !Directory.Exists(target)) Directory.Move(previous, target);
                throw;
            }
            EngineSetSelection.RegisterDirectory(id, target);
            // Prior damaged/installed sets are retained; finally removes only this call's preparation directory.
            progress?.Report(new("engine-set-" + id, id, downloadBytes, downloadBytes, 0, "verified"));
        }
        finally
        {
            try { if (staging is not null) EngineSetStageCleanup.DeleteOwned(_installedRoot, staging, stagingLeaf!); }
            finally { _gate.Release(); }
        }
    }

    private async Task<Dictionary<string, Dictionary<UpdateFile, string>>> ReusablePackagesAsync(EngineSetDefinition set, CancellationToken ct)
    {
        var result = new Dictionary<string, Dictionary<UpdateFile, string>>(StringComparer.Ordinal);
        var roots = new[] { _installedRoot, _bundledRoot }.Select(parent => SafeUpdatePath.Resolve(parent, set.Id)).ToArray();
        foreach (var package in set.VerifiedManifest.Packages)
        {
            var matches = new Dictionary<UpdateFile, string>();
            var files = set.VerifiedManifest.Files.Where(f => f.Package == package.Id).ToArray();
            foreach (var file in files)
                foreach (var root in roots)
                {
                    var path = SafeUpdatePath.Resolve(root, file.Path);
                    if (!File.Exists(path) || !await UpdatePlanner.MatchesAsync(path, file, ct)) continue;
                    matches.Add(file, path); break;
                }
            if (matches.Count == files.Length) result.Add(package.Id, matches);
        }
        return result;
    }

    private async Task<bool> MigrateLegacyAsync(EngineSetDefinition set, CancellationToken ct)
    {
        if (await VerifyAsync(set, ct)) return true;
        var migrated = false;
        var target = SafeUpdatePath.Resolve(_installedRoot, set.Id);
        foreach (var file in set.VerifiedManifest.Files)
        {
            var segments = file.Path.Split('/');
            var component = segments[0] + "/" + segments[1];
            if (!_legacyDirectories.TryGetValue(component, out var sourceRoot)) continue;
            var source = SafeUpdatePath.Resolve(sourceRoot, string.Join('/', segments.Skip(2)));
            if (!File.Exists(source) || !await UpdatePlanner.MatchesAsync(source, file, ct)) continue;
            migrated = true;
            var destination = SafeUpdatePath.Resolve(target, file.Path);
            if (File.Exists(destination) && await UpdatePlanner.MatchesAsync(destination, file, ct)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".migrating";
            await using (var input = File.OpenRead(source))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { await input.CopyToAsync(output, ct); output.Flush(true); }
            if (!await UpdatePlanner.MatchesAsync(temporary, file, ct)) throw new InvalidDataException("Legacy engine copy changed.");
            File.Move(temporary, destination, true);
        }
        return migrated;
    }

    private static EngineSetService CreateDefault()
    {
        using var stream = typeof(EngineSetService).Assembly.GetManifestResourceStream("AIHub.EngineSets.Catalog")
            ?? throw new InvalidDataException("Missing bundled engine set catalog.");
        var catalog = JsonSerializer.Deserialize<EngineSetCatalog>(stream, EngineSetCatalog.JsonOptions)
            ?? throw new InvalidDataException("Invalid bundled engine set catalog.");
        var legacy = new Dictionary<string, string>
        {
            ["studio/native"] = AppDataPaths.ProjectRoot is null ? Path.Combine(AppContext.BaseDirectory, "MusicStudioRuntime")
                : Path.Combine(AppDataPaths.RuntimeDirectory, "MusicStudio", "v3.4.0-lopata-paths-1"),
            ["llama/cpu"] = Path.Combine(AppDataPaths.ComponentRuntimesDirectory, "runtime.llama.cpu", "b9442-lopata1"),
            ["llama/vulkan"] = Path.Combine(AppDataPaths.ComponentRuntimesDirectory, "runtime.llama.vulkan", "b9442-lopata1"),
            ["sd/cpu"] = Path.Combine(AppDataPaths.ComponentRuntimesDirectory, "runtime.sd.cpu", "3f8527a-lopata1"),
            ["sd/vulkan"] = Path.Combine(AppDataPaths.ComponentRuntimesDirectory, "runtime.sd.vulkan", "3f8527a-lopata1")
        };
        return new(catalog, UpdateReleaseKeys.Trusted, Path.Combine(AppDataPaths.BaseDirectory, "engine-sets.json"),
            Path.Combine(AppDataPaths.RuntimeDirectory, "EngineSets"), Path.Combine(AppContext.BaseDirectory, "EngineSets"),
            new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
            requiredCapabilities: () => ["chat.gguf", "image.z-image", "image.flux", "music.studio"], legacyDirectories: legacy)
        { MaximumParallelConnections = new AppSettingsStore().LoadOrCreate().ModelDownloads?.MaximumParallelConnections ?? 0 };
    }
}
