using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Explicitly installed, owned one-shot workers. Processing never downloads dependencies.</summary>
public sealed partial class ImageUtilityAiService : IImageUtilityAiProcessor, IDisposable
{
    private readonly ManagedModelLibraryStore _store;
    private readonly ManagedModelAcquisitionService _downloads;
    private string? _storageRoot;
    private static readonly SemaphoreSlim InstallationGate = new(1, 1);
    public ImageUtilityAiService(ManagedModelLibraryStore? store = null)
    { _store = store ?? new(); _downloads = new(_store, segmentedMinimumBytes: 4L * 1024 * 1024); }
    public int MaximumParallelConnections { get => _downloads.MaximumParallelConnections; set => _downloads.MaximumParallelConnections = value; }
    public void ConfigureStorage(string root) => _storageRoot = string.IsNullOrWhiteSpace(root) ? null : Path.GetFullPath(root);
    public static bool UsesArtifact(string methodId, string artifactId) =>
        ImageUtilityAiCatalog.Artifacts.Any(a => a.MethodId == methodId && a.Id == artifactId)
        || methodId == "swinir" && artifactId == GigaEmbeddingInstallation.RuntimeLicenseId
        || methodId == "real-cugan" && artifactId == NcnnMsvcLicenseId;
    public static long DownloadBytes(string methodId) => ImageUtilityAiCatalog.Artifacts.Where(a => a.MethodId == methodId).Sum(a => a.Files.Sum(f => f.SizeBytes));
    public static bool NeedsSharedPython(string methodId) => methodId == "swinir" && !ManagedPythonRuntime.HasCpu;
    private static string Worker => Path.Combine(AppContext.BaseDirectory, "Tools", "image-utility-swinir.py");

    public IReadOnlyList<ManagedModelArtifactCard> Register(string methodId)
    {
        return ImageUtilityAiCatalog.CreateCards(methodId, _storageRoot).Select(card =>
        {
            var prior = _store.Load(card.ModelArtifactId);
            if (prior is not null && string.Equals(prior.InstallDirectory, card.InstallDirectory, StringComparison.OrdinalIgnoreCase))
            {
                card.Status = prior.Status; card.StoredBytes = prior.StoredBytes;
                foreach (var file in card.Files)
                {
                    var verified = prior.Files.FirstOrDefault(old => old.RelativePath == file.RelativePath
                        && old.SizeBytes == file.SizeBytes && old.Sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase));
                    if (verified is null) continue;
                    file.VerifiedSizeBytes = verified.VerifiedSizeBytes;
                    file.VerifiedLastWriteTimeUtc = verified.VerifiedLastWriteTimeUtc;
                }
            }
            return _store.Upsert(card);
        }).ToArray();
    }
    public bool IsReady(string methodId)
    {
        if (!ImageUtilityAiCatalog.IsAi(methodId)) return true;
        try
        {
            var expected = ImageUtilityAiCatalog.CreateCards(methodId, _storageRoot);
            var cards = expected.Select(c => _store.Load(c.ModelArtifactId)).ToArray();
            if (cards.Any(c => c is null)) return false;
            for (var i = 0; i < cards.Length; i++)
            {
                var current = cards[i]!;
                if (!string.Equals(current.InstallDirectory, expected[i].InstallDirectory, StringComparison.OrdinalIgnoreCase)
                    || current.Files.Count != expected[i].Files.Count
                    || expected[i].Files.Any(f => !current.Files.Any(x => x.RelativePath == f.RelativePath
                        && x.SizeBytes == f.SizeBytes && x.Sha256.Equals(f.Sha256, StringComparison.OrdinalIgnoreCase)))) return false;
            }
            return cards.All(c => c!.Status == ManagedModelStatuses.Installed && c.Files.All(f =>
            {
                var info = new FileInfo(Path.Combine(c.InstallDirectory, f.RelativePath));
                return info.Exists && (!ComponentAudit.FullRequired
                    || info.Length == f.VerifiedSizeBytes && info.LastWriteTimeUtc == f.VerifiedLastWriteTimeUtc);
            })) && ExtractionReady(cards.Single()!) && (methodId != "swinir" || ManagedPythonRuntime.HasCpu);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException) { return false; }
    }
    public async Task<bool> CheckAsync(string methodId, IProgress<ManagedModelDownloadProgress>? progress, CancellationToken token)
    {
        using var auditOperation = ComponentAudit.CaptureOperation();
        foreach (var card in Register(methodId))
            if ((await _downloads.VerifyAsync(card.ModelArtifactId, progress, token)).Status != ManagedModelStatuses.Installed) return false;
        if (!IsReady(methodId)) return false;
        if (methodId != "swinir")
        {
            var card = Register(methodId).Single();
            await ComponentLicenseGate.EnsureAsync(NativeLicenseIds(card, methodId), token);
            await VerifyNativeAsync(card, token);
        }
        if (methodId == "swinir")
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            try
            {
                await VerifySwinirImportAsync(Register(methodId).Single(), timeout.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
            catch (Exception ex) when (ex is ImageUtilityException or IOException or InvalidDataException or System.ComponentModel.Win32Exception) { return false; }
        }
        return true;
    }
    public async Task InstallAsync(string methodId, IProgress<ManagedModelDownloadProgress>? progress, CancellationToken token)
    {
        using var installation = ComponentAudit.Installation();
        await InstallationGate.WaitAsync(token);
        try
        {
            var cards = Register(methodId);
            var licenses = cards.Select(c => c.ModelArtifactId).ToList();
            if (methodId == "swinir") licenses.Add(GigaEmbeddingInstallation.RuntimeLicenseId);
            if (methodId == "real-cugan") licenses.Add(NcnnMsvcLicenseId);
            await ComponentLicenseGate.EnsureAsync(licenses, token);
            foreach (var card in cards)
            {
                var verified = await _downloads.VerifyAsync(card.ModelArtifactId, progress, token);
                if (verified.Status != ManagedModelStatuses.Installed)
                    verified = await _downloads.DownloadAsync(card.ModelArtifactId, progress, token);
                if (verified.Status != ManagedModelStatuses.Installed) throw new ImageUtilityException("ImageUtility.Ai.VerificationFailed");
                await Task.Run(() => Extract(verified, token), token);
            }
            if (methodId == "swinir")
            {
                var runtime = await ManagedPythonRuntime.ResolveAsync("cpu", token);
                await RunAsync(runtime.Python, PythonArguments(cards.Single(), "--check"), null, token);
            }
        }
        finally { InstallationGate.Release(); }
    }
    private static IEnumerable<string> PythonArguments(ManagedModelArtifactCard card, params string[] arguments)
        => new[] { Worker, "--dependencies", Path.Combine(card.InstallDirectory, "expanded") }.Concat(arguments);

    private sealed record ExtractedFile(string Path, long Length, DateTime LastWriteTimeUtc);
    private static ExtractedFile[] ExtractionFiles(ManagedModelArtifactCard card)
    {
        var stamp = Path.Combine(card.InstallDirectory, "expanded", "image-utility-ready.json");
        if (!File.Exists(stamp) || new FileInfo(stamp).Length > 4 * 1024 * 1024)
            throw new InvalidDataException("Image utility extraction inventory is missing or oversized.");
        return JsonSerializer.Deserialize<ExtractedFile[]>(File.ReadAllText(stamp)) ?? [];
    }
    private static bool ExtractionReady(ManagedModelArtifactCard card)
    {
        var root = Path.Combine(card.InstallDirectory, "expanded");
        var stamp = Path.Combine(root, "image-utility-ready.json");
        if (!File.Exists(stamp)) return false;
        var files = ExtractionFiles(card);
        return files is { Length: > 0 } && files.All(f =>
        {
            var path = Path.GetFullPath(Path.Combine(root, f.Path));
            if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
            Lopata.Updates.SafeUpdatePath.RejectLinks(path);
            var info = new FileInfo(path);
            return info.Exists && (!ComponentAudit.FullRequired || info.Length == f.Length && info.LastWriteTimeUtc == f.LastWriteTimeUtc);
        });
    }
    private static void Extract(ManagedModelArtifactCard card, CancellationToken token)
    {
        try { if (ExtractionReady(card)) return; }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException or UnauthorizedAccessException) { }
        var destination = Path.Combine(card.InstallDirectory, "expanded");
        var staging = Path.Combine(card.InstallDirectory, "extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var file in card.Files.Where(f => f.Purpose is "runtime" or "pillow"))
                ImageGenerationInstallation.ExtractArchive(Path.Combine(card.InstallDirectory, file.RelativePath), staging, token);
            var stamps = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).Select(path =>
            {
                var info = new FileInfo(path);
                return new ExtractedFile(Path.GetRelativePath(staging, path), info.Length, info.LastWriteTimeUtc);
            }).ToArray();
            File.WriteAllText(Path.Combine(staging, "image-utility-ready.json"), JsonSerializer.Serialize(stamps));
            // Preserve the previous extraction until the new one is complete; never touch model sources.
            if (Directory.Exists(destination)) Directory.Move(destination, destination + ".old-" + Guid.NewGuid().ToString("N"));
            Directory.Move(staging, destination);
            ComponentAudit.Invalidate(destination);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
    private static string NativeExecutable(ManagedModelArtifactCard card, string methodId)
    {
        var name = methodId == "real-esrgan" ? "realesrgan-ncnn-vulkan.exe" : "realcugan-ncnn-vulkan.exe";
        var file = ExtractionFiles(card).Single(file => Path.GetFileName(file.Path).Equals(name, StringComparison.OrdinalIgnoreCase));
        return PinnedZipExpansionVerifier.ResolveArchivePath(Path.Combine(card.InstallDirectory, "expanded"), file.Path.Replace('\\', '/'));
    }

    private static Task VerifySwinirImportAsync(ManagedModelArtifactCard card, CancellationToken token) =>
        ComponentAudit.RunAsync("image-utility-swinir-import", Path.Combine(card.InstallDirectory, "expanded"),
            card.Revision + "/" + string.Join('\n', card.Files.Select(file => file.RelativePath + "|" + file.Sha256)),
            cancellation => {
                cancellation.ThrowIfCancellationRequested();
                if (!ExtractionReady(card) || !File.Exists(Worker)) throw new InvalidDataException("Image utility dependencies are missing.");
                return Task.CompletedTask;
            }, async cancellation => {
                var runtime = await ManagedPythonRuntime.ResolveAsync("cpu", cancellation);
                await RunAsync(runtime.Python, PythonArguments(card, "--check"), null, cancellation);
            }, token);

    private static async Task RunAsync(string executable, IEnumerable<string> arguments,
        IProgress<ImageUtilityProgress>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileName(executable).Equals("python.exe", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var key in info.Environment.Keys.Where(key => key.StartsWith("PYTHON", StringComparison.OrdinalIgnoreCase)).ToArray())
                info.Environment.Remove(key);
            ManagedPythonLaunch.ScriptArguments(info, arguments);
        }
        else foreach (var value in arguments) info.ArgumentList.Add(value);
        info.Environment["PYTHONUTF8"] = "1"; info.Environment["PYTHONUNBUFFERED"] = "1";
        info.Environment["HF_HUB_OFFLINE"] = "1";
        using var process = OwnedProcessRegistry.Shared.Start(info, "ImageUtility.AI");
        var errors = new System.Text.StringBuilder();
        async Task Pump(StreamReader reader)
        {
            while (await reader.ReadLineAsync(token) is { } line)
            {
                lock (errors) { if (errors.Length < 12000) errors.AppendLine(line); }
                if (line.StartsWith("LOPATA_PROGRESS ", StringComparison.Ordinal)
                    && double.TryParse(line[16..], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var fraction))
                    progress?.Report(new("ImageUtility.Ai.Processing", Fraction: Math.Clamp(fraction, 0, 1)));
            }
        }
        var stdout = Pump(process.StandardOutput); var stderr = Pump(process.StandardError);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(token), stdout, stderr);
            if (process.ExitCode != 0) throw new ImageUtilityException("ImageUtility.Ai.WorkerFailed", errors.ToString(), retryable: true);
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { }
        }
    }
    public void Dispose() => _downloads.Dispose();
}
