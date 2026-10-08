using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class PinnedFileTreeVerifierTests
{
    public TestContext TestContext { get; set; } = null!;
    private static PinnedTreeFile Entry(string path, string text) => new(path, System.Text.Encoding.UTF8.GetByteCount(text),
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))));
    [TestMethod]
    public async Task HashesRejectSameSizeSameTimestampTamperingAndReleaseLeases()
    {
        using var files = new MusicProjectTests.Files(); var root = Path.Combine(files.Root, "tree"); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "a.py"); await File.WriteAllTextAsync(path, "original");
        var entries = new[] { Entry("a.py", "original") }; var stamp = File.GetLastWriteTimeUtc(path);
        await PinnedFileTreeVerifier.VerifyAsync(root, entries, default);
        await File.WriteAllTextAsync(path, "modified"); File.SetLastWriteTimeUtc(path, stamp);
        await Assert.ThrowsAsync<InvalidDataException>(() => PinnedFileTreeVerifier.VerifyAsync(root, entries, default));
        Directory.Move(root, root + "-moved");
    }
    [TestMethod]
    public async Task AddedMissingAndUnsafePathsCannotPass()
    {
        using var files = new MusicProjectTests.Files(); var root = Path.Combine(files.Root, "tree"); Directory.CreateDirectory(root);
        var entries = new[] { Entry("a.py", "ok") }; await File.WriteAllTextAsync(Path.Combine(root, "a.py"), "ok");
        await Assert.ThrowsAsync<InvalidDataException>(() => PinnedFileTreeVerifier.VerifyAsync(root, entries, default,
            (_, _) => File.WriteAllText(Path.Combine(root, "extra.py"), "extra")));
        File.Delete(Path.Combine(root, "extra.py")); File.Delete(Path.Combine(root, "a.py"));
        await Assert.ThrowsAsync<InvalidDataException>(() => PinnedFileTreeVerifier.VerifyAsync(root, entries, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => PinnedFileTreeVerifier.VerifyAsync(root, [Entry("../a.py", "ok")], default));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => PinnedFileTreeVerifier.VerifyAsync(root, entries, cancel.Token));
    }
    [TestMethod]
    public async Task DirectoryCannotBeSwappedDuringVerification()
    {
        using var files = new MusicProjectTests.Files(); var root = Path.Combine(files.Root, "tree");
        var nested = Path.Combine(root, "nested"); Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(nested, "a.py"), "ok"); Exception? renameFailure = null;
        await PinnedFileTreeVerifier.VerifyAsync(root, [Entry("nested/a.py", "ok")], default, (_, _) => {
            try { Directory.Move(nested, nested + "-renamed"); } catch (IOException error) { renameFailure = error; }
        });
        Assert.IsNotNull(renameFailure); Assert.IsTrue(Directory.Exists(nested));
        Directory.Move(nested, nested + "-renamed");
    }
    [TestMethod]
    public async Task CancellationDuringHashingReleasesDirectoryLeases()
    {
        using var files = new MusicProjectTests.Files(); var root = Path.Combine(files.Root, "tree"); Directory.CreateDirectory(root);
        foreach (var name in new[] { "a.py", "b.py" }) await File.WriteAllTextAsync(Path.Combine(root, name), "ok");
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(() => PinnedFileTreeVerifier.VerifyAsync(root,
            [Entry("a.py", "ok"), Entry("b.py", "ok")], cancel.Token, (_, _) => cancel.Cancel(), parallelism: 1));
        Directory.Move(root, root + "-moved");
    }
    [TestMethod]
    public async Task JunctionIsRejectedWithoutFollowingIt()
    {
        using var files = new MusicProjectTests.Files(); var root = Path.Combine(files.Root, "tree"); Directory.CreateDirectory(root);
        var target = Path.Combine(files.Root, "target"); Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "a.py"), "ok");
        var link = Path.Combine(root, "nested");
        var info = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!; await process.WaitForExitAsync();
        Assert.AreEqual(0, process.ExitCode);
        try { await Assert.ThrowsAsync<InvalidDataException>(() => PinnedFileTreeVerifier.VerifyAsync(root, [Entry("nested/a.py", "ok")], default)); }
        finally { Directory.Delete(link); }
    }
    [TestMethod]
    public async Task LiveInstalledTreesBenchmarkWithoutModelInference()
    {
        if (Environment.GetEnvironmentVariable("AIHUB_ACE_VERIFY_BENCHMARK") != "1") Assert.Inconclusive("Explicit read-only verification benchmark required.");
        var manager = new ComponentManager();
        var cuda = manager.GetInstallDirectory(ComponentCatalog.Find(HardwareRuntimeCatalog.PythonCudaId)!);
        var overlay = Path.Combine(AppDataPaths.BaseDirectory, "Music", "Python", MusicAceCatalog.RuntimeRevision);
        var rows = new List<object>();
        foreach (var (name, root, manifestPath) in new[] {
            ("ACE", overlay, Path.Combine("Tools", "ace-xl-python-files.json")),
            ("CUDA126", cuda, Path.Combine("Tools", "python-hardware-cu126-files.json")) }) {
            using var manifest = name == "ACE"
                ? JsonDocument.Parse(typeof(MusicAceCatalog).Assembly.GetManifestResourceStream("AIHub.AcePythonManifest")!)
                : JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, manifestPath)));
            var entries = manifest.RootElement.GetProperty("files").EnumerateArray().Select(row => new PinnedTreeFile(
                row.GetProperty("path").GetString()!, row.GetProperty("size").GetInt64(), row.GetProperty("sha256").GetString()!)).ToArray();
            foreach (var degree in new[] { 1, 2, 1, 2 }) {
                var watch = Stopwatch.StartNew();
                await PinnedFileTreeVerifier.VerifyAsync(root, entries, default, parallelism: degree,
                    ignoredFiles: name == "ACE" ? new HashSet<string> { "files.json" } : null);
                rows.Add(new { name, degree, files = entries.Length, elapsedMs = watch.ElapsedMilliseconds });
                TestContext.WriteLine($"{name}: {entries.Length} files; degree={degree}; {watch.ElapsedMilliseconds}ms");
            }
        }
        var destination = Environment.GetEnvironmentVariable("AIHUB_ACE_VERIFY_REPORT");
        if (destination is not null) await File.WriteAllTextAsync(destination, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
    }
    [TestMethod]
    public async Task LiveACEPreparationChecksAPIWithoutLoadingModelWeights()
    {
        var root = Environment.GetEnvironmentVariable("AIHUB_ACE_PREPARE_ROOT");
        if (string.IsNullOrWhiteSpace(root)) Assert.Inconclusive("Explicit existing model root required; no download or inference.");
        var previous = ComponentLicenseGate.ConfirmAsync;
        var licenses = new ComponentLicenseService(Path.Combine(AppContext.BaseDirectory, "Licenses"),
            Path.Combine(AppDataPaths.BaseDirectory, "Licenses", "receipts.json"));
        ComponentLicenseGate.ConfirmAsync = (ids, token) => licenses.EnsureAsync(ids, _ => Task.FromResult(false), token);
        var report = new List<object>();
        try {
            for (var attempt = 1; attempt <= 2; attempt++) {
                var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
                var watch = Stopwatch.StartNew();
                var receipt = await MusicAceWorker.ProbeAsync(root, default, log.Enqueue);
                Assert.HasCount(3, receipt.Cards); Assert.IsTrue(receipt.Cards.All(card => card.Status == "installed"));
                report.Add(new { attempt, elapsedMs = watch.ElapsedMilliseconds, receipt.Hardware, stages = log.Where(line => line.Contains("elapsedMs=", StringComparison.Ordinal)).ToArray() });
                TestContext.WriteLine($"ACE complete preparation attempt {attempt}: {watch.ElapsedMilliseconds}ms; {receipt.Hardware}");
            }
        }
        finally { ComponentLicenseGate.ConfirmAsync = previous; }
        var destination = Environment.GetEnvironmentVariable("AIHUB_ACE_PREPARE_REPORT");
        if (destination is not null) await File.WriteAllTextAsync(destination, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
