using System.Diagnostics;
using System.IO;
using System.Net.Http;
using AIHub.Models;
using AIHub.Services.Books.Smart;
using Lopata.Updates;

namespace AIHub.Services.ImageLayers;

internal static class LayerRasterRuntime
{
    internal const string LicenseId = "runtime.image-layers-opencv";
    internal const string PortraitLicenseId = "runtime.image-layers-portrait";
    private static readonly SemaphoreSlim Gate = new(1);
    private static string DirectoryPath(bool portrait) => Path.Combine(AppDataPaths.RuntimeDirectory, "ImageLayers",
        portrait ? "py31210-portrait-1" : "py31210-cv410");
    internal static string Python => Path.Combine(DirectoryPath(false), "python.exe");
    internal static string PortraitPython => Path.Combine(DirectoryPath(true), "python.exe");
    private static string Cache => Path.Combine(AppDataPaths.ComponentDownloadsDirectory, "ImageLayers");

    internal static async Task<bool> PrepareAsync(bool download, IProgress<ManagedModelDownloadProgress>? progress,
        CancellationToken token, bool portrait = false)
    {
        await Gate.WaitAsync(token);
        try
        {
            var artifacts = BookSmartRuntimeManifest.Artifacts(new("paddle", "alpha"))
                .Where(item => item.Name is "numpy" or "opencv-contrib-python" || item == PythonBootstrapArtifacts.Python)
                .ToArray();
            if (artifacts.Length != 3) throw new InvalidDataException("ImageLayers.Components");
            if (portrait) artifacts = [.. artifacts, .. LayerPortraitRuntimeManifest.Wheels];
            var directory = DirectoryPath(portrait);
            var python = Path.Combine(directory, "python.exe");
            var license = portrait ? PortraitLicenseId : LicenseId;
            var manager = new ComponentManager();
            var msvcEntry = ComponentCatalog.Find(HardwareRuntimeCatalog.LlamaCpuId)!;
            var msvc = manager.GetStatus().First(status => status.Entry.Id == msvcEntry.Id);
            if (!msvc.IsAvailable && download)
                msvc = await manager.DownloadAndInstallAsync(msvcEntry.Id, null, token);
            if (!msvc.IsAvailable) return false;
            var native = manager.GetInstallDirectory(msvcEntry);
            var identity = BookSmartRuntimeManifest.LockDigest + ":opencv:" +
                string.Join(";", artifacts.Select(item => item.Sha256));
            if (portrait) identity += ":" + LayerPortraitRuntimeManifest.Identity;
            await ComponentLicenseGate.EnsureAsync(portrait ? [LicenseId, license] : [license], token);
            await HardwareRuntimeBundleVerifier.VerifyFilesAsync(msvcEntry.Id, native, token);

            if (File.Exists(python))
            {
                try
                {
                    await ComponentAudit.RunAsync(license, directory, identity,
                        ct => RuntimeFileAudit.PresenceInventoryAsync(directory, identity, ct), async ct =>
                        {
                            var files = await BookSmartRuntimeManifest.ExpectedFilesAsync(artifacts, Cache, native, ct);
                            await PinnedFileTreeVerifier.VerifyAsync(directory, files, ct);
                            await ProbeAsync(python, portrait, ct);
                            await RuntimeFileAudit.SaveInventoryAsync(directory, identity, files.Select(file => file.Path), ct);
                        }, token);
                    return true;
                }
                catch (IOException) when (download) { }
                catch (InvalidDataException) when (download) { }
                catch (IOException) { return false; }
                catch (InvalidDataException) { return false; }
            }

            Directory.CreateDirectory(Cache);
            SafeUpdatePath.RejectLinks(Cache);
            var shared = Path.Combine(AppDataPaths.ComponentDownloadsDirectory, "BookSmart", "paddle");
            foreach (var item in artifacts)
            {
                token.ThrowIfCancellationRequested();
                var local = Path.Combine(Cache, item.FileName);
                var cached = Path.Combine(shared, item.FileName);
                if (await BookSmartRuntimeManifest.MatchesAsync(local, item, token)) continue;
                if (await BookSmartRuntimeManifest.MatchesAsync(cached, item, token))
                    File.Copy(cached, local, overwrite: true);
                else if (!download) return false;
            }
            using var http = new HttpClient();
            var sources = download ? await PinnedPythonDownloader.AcquireAsync(artifacts, Cache, http,
                (done, total) => progress?.Report(new(license, portrait ? "MediaPipe / PyMatting / ONNX" : "OpenCV", done, total, 0, "downloading")), token)
                : artifacts.ToDictionary(item => item.FileName, item => Path.Combine(Cache, item.FileName));
            var expected = await BookSmartRuntimeManifest.ExpectedFilesAsync(artifacts, Cache, native, token);
            await InstallAsync(artifacts, sources, expected, native, identity, portrait, token);
            return true;
        }
        finally { Gate.Release(); }
    }

    private static async Task InstallAsync(IReadOnlyList<PinnedPythonArtifact> artifacts,
        IReadOnlyDictionary<string, string> sources, IReadOnlyList<PinnedTreeFile> expected, string native, string identity,
        bool portrait, CancellationToken token)
    {
        var directory = DirectoryPath(portrait);
        var stage = directory + ".installing-" + Guid.NewGuid().ToString("N");
        var previous = directory + ".previous-" + Guid.NewGuid().ToString("N");
        SafeUpdatePath.RejectLinks(directory);
        SafeUpdatePath.RejectLinks(stage);
        Directory.CreateDirectory(stage);
        try
        {
            foreach (var item in artifacts)
                await PythonWheelExtractor.ExtractAsync(item, sources[item.FileName], stage, token,
                    wheelLayout: item != PythonBootstrapArtifacts.Python);
            await File.WriteAllTextAsync(Path.Combine(stage, "python312._pth"), BookSmartRuntimeManifest.PythonPathConfiguration, token);
            var notices = Directory.CreateDirectory(Path.Combine(stage, "Notices")).FullName;
            File.Copy(BookSmartRuntimeManifest.LockPath, Path.Combine(notices, "wheel-lock.json"));
            foreach (var dll in new[] { "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll" })
                File.Copy(Path.Combine(native, dll), Path.Combine(stage, dll), overwrite: true);
            await PinnedFileTreeVerifier.VerifyAsync(stage, expected, token);
            await ProbeAsync(Path.Combine(stage, "python.exe"), portrait, token);
            token.ThrowIfCancellationRequested();
            if (Directory.Exists(directory)) Directory.Move(directory, previous);
            try
            {
                Directory.Move(stage, directory);
                ComponentAudit.Invalidate(directory);
            }
            catch
            {
                if (!Directory.Exists(directory) && Directory.Exists(previous)) Directory.Move(previous, directory);
                throw;
            }
        }
        finally
        {
            if (Directory.Exists(stage))
            {
                SafeUpdatePath.RejectLinks(stage);
                Directory.Delete(stage, recursive: true);
            }
        }
        using var installation = ComponentAudit.Installation();
        await ComponentAudit.RunAsync(portrait ? PortraitLicenseId : LicenseId, directory, identity,
            ct => RuntimeFileAudit.PresenceInventoryAsync(directory, identity, ct), async ct =>
            {
                await PinnedFileTreeVerifier.VerifyAsync(directory, expected, ct);
                await RuntimeFileAudit.SaveInventoryAsync(directory, identity, expected.Select(file => file.Path), ct);
            }, token);
    }

    internal static async Task RunAsync(string python, string folder, string code, IEnumerable<string> args,
        CancellationToken token, string processOwner = "ImageLayers.Raster", bool portrait = false)
    {
        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder,
            RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (var value in new[] { "-I", "-B", "-c", code }.Concat(args)) start.ArgumentList.Add(value);
        if (portrait)
        {
            var cache = Path.Combine(AppDataPaths.RuntimeDirectory, "ImageLayers", "PortraitCache");
            SafeUpdatePath.RejectLinks(cache);
            Directory.CreateDirectory(cache);
            start.Environment["NUMBA_CACHE_DIR"] = Path.Combine(cache, "numba");
            start.Environment["MPLCONFIGDIR"] = Path.Combine(cache, "matplotlib");
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        using var process = OwnedProcessRegistry.Shared.Start(start, processOwner);
        var errors = PythonRuntimeHealthProbe.ReadBoundedAsync(process.StandardError, deadline.Token);
        var output = PythonRuntimeHealthProbe.ReadBoundedAsync(process.StandardOutput, deadline.Token);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), errors, output);
            if (process.ExitCode == 0) return;
            OwnedProcessRegistry.Log("image_layers_raster_error", processOwner, detail: await errors);
            throw new IOException("ImageLayers.RasterFailed");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("ImageLayers.Timeout"); }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
        }
    }

    private static Task ProbeAsync(string python, bool portrait, CancellationToken token) =>
        RunAsync(python, Path.GetDirectoryName(python)!,
            "import cv2,numpy; assert cv2.__version__ == '4.10.0' and numpy.__version__ == '2.3.5'" +
            (portrait ? "; import mediapipe,pymatting,onnxruntime; assert mediapipe.__version__ == '1.2.3' and pymatting.__version__ == '1.1.16' and onnxruntime.__version__ == '1.23.2'" : ""),
            [], token, portrait: portrait);
}
