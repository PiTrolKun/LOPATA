using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using AIHub.Models;

namespace AIHub.Services.Books.Smart;

/// <summary>Private portable Python; it never copies a venv or modifies a shared Python/Torch installation.</summary>
internal sealed class BookSmartOcrRuntime(Func<string, string> localize)
{
    private static readonly SemaphoreSlim Gate = new(1);
    internal static string WorkerPath => Path.Combine(AppContext.BaseDirectory, "Tools", "BookSmart", "ocr_worker.py");
    internal static string DirectoryFor(BookSmartSelection selection) => Path.Combine(AppDataPaths.RuntimeDirectory, "BookSmart",
        selection.OcrId switch { "paddle" => "py31210-paddle321-ocr360", "ovis" => "py31210-ovis2-pillow1211", _ => throw new ArgumentException("Unknown OCR selection.", nameof(selection)) });
    internal static string Python(BookSmartSelection selection) => Path.Combine(DirectoryFor(selection), "python.exe");
    private static string CacheFor(BookSmartSelection selection) => Path.Combine(AppDataPaths.ComponentDownloadsDirectory, "BookSmart", selection.OcrId);
    private string L(string key) => localize("BookConverter.SmartPreparation." + key);

    internal async Task<BookSmartPreparationItem[]> PrepareRuntimeAsync(BookSmartSelection selection, bool download,
        IProgress<string> progress, CancellationToken token)
    {
        using var auditOperation = ComponentAudit.CaptureOperation();
        using var auditLog = ComponentAudit.CaptureLog(progress.Report);
        await Gate.WaitAsync(token);
        try
        {
            var artifacts = BookSmartRuntimeManifest.Artifacts(selection);
            var directory = DirectoryFor(selection); var cache = CacheFor(selection);
            long missing = 0;
            foreach (var artifact in artifacts)
                if (!File.Exists(Path.Combine(cache, artifact.FileName))) missing += artifact.Bytes;
            var msvc = await MsvcAsync(selection, download, token);
            if (selection.OcrId == "paddle" && msvc is null)
                missing += ComponentCatalog.Find(HardwareRuntimeCatalog.LlamaCpuId)!.DownloadSizeBytes;
            var ready = false;
            if (Directory.Exists(directory) && (selection.OcrId != "paddle" || msvc is not null))
            {
                try
                {
                    await EnsureLicenseAsync(selection, token);
                    await VerifyInstalledAsync(selection, artifacts, cache, msvc, progress, token);
                    ready = true; missing = 0;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { ready = false; }
            }
            if (!ready && download)
            {
                await ComponentLicenseGate.EnsureAsync(selection.OcrId == "paddle"
                    ? ["runtime.book-smart-python", "runtime.book-smart-paddle"] : ["runtime.book-smart-python", "runtime.book-smart-pillow"], token);
                using (ComponentAudit.Installation())
                {
                    using var client = new HttpClient();
                    var sources = await PinnedPythonDownloader.AcquireAsync(artifacts, cache, client,
                        (done, total) => progress.Report(string.Format(L("Transfer"), L("Runtime"), done / 1048576d, total / 1048576d)), token);
                    await InstallAsync(selection, artifacts, sources, msvc, progress, token);
                }
                // Installation validation is not a first-use receipt. Establish it now, before using this runtime.
                await VerifyInstalledAsync(selection, artifacts, cache, msvc, progress, token);
                ready = true; missing = 0;
            }
            return [new(L("Runtime"), L(ready ? (ComponentAudit.FullRequired ? "FilesVerified" : "FilesPresent") : "RuntimeMissing"), missing, ready),
                new(L("Worker"), L(File.Exists(WorkerPath) ? "FilesPresent" : "RuntimeMissing"), 0, File.Exists(WorkerPath))];
        }
        finally { Gate.Release(); }
    }

    private static Task EnsureLicenseAsync(BookSmartSelection selection, CancellationToken token) =>
        ComponentLicenseGate.EnsureAsync(selection.OcrId == "paddle"
            ? ["runtime.book-smart-python", "runtime.book-smart-paddle"] : ["runtime.book-smart-python", "runtime.book-smart-pillow"], token);

    private static string InventoryIdentity(BookSmartSelection selection, IReadOnlyList<PinnedPythonArtifact> artifacts) =>
        BookSmartRuntimeManifest.LockDigest + ":" + selection.OcrId + ":" +
        string.Join(";", artifacts.Select(a => a.FileName + "|" + a.Bytes + "|" + a.Sha256));
    internal static Task<string[]> KnownFilesAsync(BookSmartSelection selection, CancellationToken token) =>
        RuntimeFileAudit.InventoryFilesAsync(DirectoryFor(selection),
            InventoryIdentity(selection, BookSmartRuntimeManifest.Artifacts(selection)), token);

    private Task VerifyInstalledAsync(BookSmartSelection selection, IReadOnlyList<PinnedPythonArtifact> artifacts,
        string cache, string? msvc, IProgress<string> progress, CancellationToken token)
    {
        var directory = DirectoryFor(selection);
        var identity = InventoryIdentity(selection, artifacts);
        return ComponentAudit.RunAsync("Books.Smart.OCR." + selection.OcrId, directory, identity,
            ct => RuntimeFileAudit.PresenceInventoryAsync(directory, identity, ct), async ct =>
            {
                var expected = await BookSmartRuntimeManifest.ExpectedFilesAsync(artifacts, cache, msvc, ct);
                await PinnedFileTreeVerifier.VerifyAsync(directory, expected, ct);
                progress.Report(L("RuntimeProbe"));
                await ProbeAsync(selection, directory, ct);
                await RuntimeFileAudit.SaveInventoryAsync(directory, identity, expected.Select(f => f.Path), ct);
            }, token, progress.Report);
    }

    private static async Task<string?> MsvcAsync(BookSmartSelection selection, bool download, CancellationToken token)
    {
        if (selection.OcrId != "paddle") return null;
        var manager = new ComponentManager();
        var entry = ComponentCatalog.Find(HardwareRuntimeCatalog.LlamaCpuId)!;
        var status = manager.GetStatus().Single(value => value.Entry.Id == entry.Id);
        if (!status.IsAvailable && download) status = await manager.DownloadAndInstallAsync(entry.Id, null, token);
        if (!status.IsAvailable) return null;
        var directory = manager.GetInstallDirectory(entry);
        await HardwareRuntimeBundleVerifier.VerifyFilesAsync(entry.Id, directory, token);
        return directory;
    }

    private async Task InstallAsync(BookSmartSelection selection, IReadOnlyList<PinnedPythonArtifact> artifacts,
        IReadOnlyDictionary<string, string> sources, string? msvc, IProgress<string> progress, CancellationToken token)
    {
        if (selection.OcrId == "paddle" && msvc is null) throw new IOException(L("RuntimeMissing"));
        var directory = DirectoryFor(selection);
        var stage = directory + ".installing-" + Guid.NewGuid().ToString("N");
        var previous = directory + ".previous-" + Guid.NewGuid().ToString("N");
        CheckPath(stage); CheckPath(directory); CheckPath(previous);
        Directory.CreateDirectory(stage);
        try
        {
            foreach (var artifact in artifacts)
            {
                progress.Report(string.Format(L("CheckFile"), artifact.Name));
                await PythonWheelExtractor.ExtractAsync(artifact, sources[artifact.FileName], stage, token,
                    wheelLayout: artifact != PythonBootstrapArtifacts.Python);
            }
            await File.WriteAllTextAsync(Path.Combine(stage, "python312._pth"), BookSmartRuntimeManifest.PythonPathConfiguration, token);
            var notices = Path.Combine(stage, "Notices"); Directory.CreateDirectory(notices);
            File.Copy(BookSmartRuntimeManifest.LockPath, Path.Combine(notices, "wheel-lock.json"));
            if (selection.OcrId == "paddle")
            {
                foreach (var name in new[] { "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll" })
                    File.Copy(Path.Combine(msvc!, name), Path.Combine(stage, name), overwrite: true);
            }
            var expected = await BookSmartRuntimeManifest.ExpectedFilesAsync(artifacts, CacheFor(selection), msvc, token);
            await PinnedFileTreeVerifier.VerifyAsync(stage, expected, token);
            await ProbeAsync(selection, stage, token); token.ThrowIfCancellationRequested();
            if (Directory.Exists(directory)) Directory.Move(directory, previous);
            try { Directory.Move(stage, directory); ComponentAudit.Invalidate(directory); }
            catch { if (Directory.Exists(previous)) Directory.Move(previous, directory); throw; }
            // Keep the replaced runtime for a manual rollback; never discard it as a side effect of preparation.
        }
        finally { if (Directory.Exists(stage)) { CheckPath(stage); Directory.Delete(stage, true); } }
    }

    private static void CheckPath(string path)
    {
        var root = Path.GetFullPath(Path.Combine(AppDataPaths.RuntimeDirectory, "BookSmart")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Book runtime stage escapes its private store.");
        Lopata.Updates.SafeUpdatePath.RejectLinks(path);
    }

    private static async Task ProbeAsync(BookSmartSelection selection, string directory, CancellationToken token)
    {
        var info = new ProcessStartInfo(Path.Combine(directory, "python.exe"))
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var key in info.Environment.Keys.Where(key => key.StartsWith("PYTHON", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(key);
        info.Environment["HF_HUB_OFFLINE"] = "1"; info.Environment["PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK"] = "True";
        info.Environment["PADDLE_PDX_CACHE_HOME"] = Path.Combine(AppDataPaths.RuntimeDirectory, "BookSmartCache");
        var check = selection.OcrId == "paddle" ? "import paddle,paddleocr,paddlex; assert paddle.__version__=='3.2.1'; assert paddleocr.__version__=='3.6.0'; "
            : "import urllib.request,json,PIL; from PIL import Image; assert PIL.__version__=='12.1.1'; Image.new('RGB',(2,2)).crop((0,0,1,1)); ";
        foreach (var argument in new[] { "-I", "-B", "-c", check + "print('LOPATA_BOOK_RUNTIME_OK')" }) info.ArgumentList.Add(argument);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(2));
        using var process = OwnedProcessRegistry.Shared.Start(info, "BookSmart.RuntimeProbe");
        var output = PythonRuntimeHealthProbe.ReadBoundedAsync(process.StandardOutput, timeout.Token);
        var errors = PythonRuntimeHealthProbe.ReadBoundedAsync(process.StandardError, timeout.Token);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), output, errors);
            if (process.ExitCode != 0 || !(await output).Contains("LOPATA_BOOK_RUNTIME_OK", StringComparison.Ordinal))
                throw new InvalidDataException($"Book runtime probe failed ({process.ExitCode}): {await errors}");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("Book runtime probe timed out."); }
        finally { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); }
    }
}
