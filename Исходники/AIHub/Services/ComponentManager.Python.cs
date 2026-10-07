using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public sealed partial class ComponentManager
{
    // Called under the shared installation gate, after the ordinary dependency/license gate.
    private async Task<ComponentStatusSnapshot> InstallPythonProfileAsync(ComponentCatalogEntry entry,
        IProgress<ComponentDownloadProgress>? progress, CancellationToken token)
    {
        var profile = HardwareRuntimeCatalog.PythonProfile(entry.Id);
        var state = _stateStore.Load();
        var original = JsonSerializer.Deserialize<ComponentInstallationRecord>(
            JsonSerializer.Serialize(FindRecord(state, entry)))!;
        var record = JsonSerializer.Deserialize<ComponentInstallationRecord>(JsonSerializer.Serialize(original))!;
        var cacheDirectory = Path.Combine(AppDataPaths.ComponentDownloadsDirectory, entry.FileName);
        var directory = GetInstallDirectory(entry);
        var stage = directory + ".installing";
        var previous = directory + ".previous";
        // Recover an interrupted swap conservatively. The old directory is never thrown away on restart.
        RejectPythonPaths(directory, stage, previous, cacheDirectory);
        if (Directory.Exists(previous))
        {
            var keepCurrent = false;
            if (original.Version == entry.Version && original.VerifiedAt is not null
                && original.ComputedSha256.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(directory))
            {
                try { await PythonRuntimeBundleVerifier.VerifyAsync(profile, directory, token); keepCurrent = true; }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { }
            }
            if (keepCurrent) DeleteContainedDirectory(previous);
            else
            {
                DeleteContainedDirectory(directory);
                Directory.Move(previous, directory);
            }
        }
        var committed = false;
        var clock = Stopwatch.StartNew();
        void Report(string phase, long downloaded, long total) => progress?.Report(
            new(entry.Id, downloaded, total, downloaded / Math.Max(.1, clock.Elapsed.TotalSeconds), phase));
        try
        {
            record.Status = ComponentInstallStatuses.Downloading;
            record.TotalBytes = entry.DownloadSizeBytes;
            record.LastError = string.Empty;
            Upsert(state, record); _stateStore.Save(state);
            _eventLog.Write("component_download_started", new { entry.Id, entry.Version, entry.Source, entry.DownloadSizeBytes });
            var artifacts = PythonBootstrapArtifacts.DownloadSet(profile);
            if (artifacts.Sum(artifact => artifact.Bytes) != entry.DownloadSizeBytes)
                throw new InvalidDataException("Python profile download size differs from the catalog.");
            var cache = await PinnedPythonDownloader.AcquireAsync(artifacts, cacheDirectory, HttpClient,
                (done, total) => Report(ComponentInstallStatuses.Downloading, done, total), token);
            record.DownloadedBytes = entry.DownloadSizeBytes;
            record.DownloadedAt = DateTimeOffset.Now;
            record.Status = ComponentInstallStatuses.Downloaded;
            Upsert(state, record); _stateStore.Save(state);
            _eventLog.Write("component_download_completed", new { entry.Id, Bytes = record.DownloadedBytes, Artifacts = artifacts.Count });

            Report(ComponentInstallStatuses.NeedsVerification, 0, 0);
            DeleteContainedDirectory(stage);
            Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
            var dependency = ComponentCatalog.Find(HardwareRuntimeCatalog.LlamaCpuId)!;
            await PythonRuntimeStaging.PrepareAsync(profile, stage, cache, GetInstallDirectory(dependency), token);
            await PythonRuntimeBundleVerifier.VerifyAsync(profile, stage, token);
            await PythonRuntimeHealthProbe.VerifyAsync(profile, stage, token);
            token.ThrowIfCancellationRequested();

            RejectPythonPaths(directory, stage, previous, cacheDirectory);
            if (Directory.Exists(directory)) Directory.Move(directory, previous);
            try
            {
                Directory.Move(stage, directory);
                record.Version = entry.Version;
                record.InstallPath = directory;
                record.DownloadPath = string.Empty; // The cache is a set of archives, not a single file.
                record.ComputedSha256 = entry.Sha256;
                record.InstalledAt = record.VerifiedAt = DateTimeOffset.Now;
                record.Status = ComponentInstallStatuses.Installed;
                record.LastError = string.Empty;
                Upsert(state, record); _stateStore.Save(state);
                var installed = GetStatus().First(item => item.Entry.Id == entry.Id);
                if (!installed.IsAvailable) throw new InvalidDataException("Python profile did not pass installed layout checks.");
                _eventLog.Write("component_verified", new { entry.Id, record.InstallPath, record.ComputedSha256 });
                committed = true;
                // The commit is complete; cleanup failure must not roll back a validated installation.
                try { DeleteContainedDirectory(previous); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { _eventLog.Write("component_cleanup_failed", new { entry.Id, error.Message }); }
                Report(ComponentInstallStatuses.Installed, entry.DownloadSizeBytes, entry.DownloadSizeBytes);
                return installed;
            }
            catch
            {
                if (!committed)
                {
                    DeleteContainedDirectory(directory);
                    if (Directory.Exists(previous)) Directory.Move(previous, directory);
                }
                throw;
            }
        }
        catch
        {
            // Preserve the previous installation receipt, even if acquisition or state persistence failed.
            if (!committed)
            {
                var restored = _stateStore.Load(); Upsert(restored, original); _stateStore.Save(restored);
            }
            throw;
        }
        finally
        {
            try { DeleteContainedDirectory(stage); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { _eventLog.Write("component_cleanup_failed", new { entry.Id, error.Message }); }
        }
    }

    private static void RejectPythonPaths(params string[] paths)
    {
        foreach (var path in paths)
        {
            var root = Path.GetFullPath(AppDataPaths.ComponentsDirectory).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Python installation path escapes the component store.");
            Lopata.Updates.SafeUpdatePath.RejectLinks(path);
        }
    }
}
