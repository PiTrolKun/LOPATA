using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class PythonWheelExtractorTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task CompleteCpuStageUsesOnlyPinnedArchivesAndVerifiedMicrosoftLibraries()
    {
        var source = Environment.GetEnvironmentVariable("AIHUB_PYTHON_WHEEL_CACHE");
        var stand = Environment.GetEnvironmentVariable("AIHUB_LLAMA_HARDWARE_STAND");
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(stand))
            Assert.Inconclusive("Explicit wheel cache and isolated stand required.");
        var cache = PinnedPythonWheelSet.Load().Wheels.ToDictionary(artifact => artifact.FileName,
            artifact => Path.Combine(source, artifact.FileName));
        cache.Add(PythonBootstrapArtifacts.Python.FileName, Path.Combine(GigaEmbeddingInstallation.LegacyRoot, "python.zip"));
        cache.Add(PythonBootstrapArtifacts.Pip.FileName, Path.Combine(GigaEmbeddingInstallation.LegacyRoot, "pip.whl"));
        var stage = Path.GetFullPath(Path.Combine(stand, "python-direct-runtime", Guid.NewGuid().ToString("N") + ".installing"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var msvc = new ComponentManager().GetInstallDirectory(ComponentCatalog.Find(HardwareRuntimeCatalog.LlamaCpuId)!);
        await PythonRuntimeStaging.PrepareCpuAsync(stage, cache, msvc, timeout.Token);
        Assert.IsTrue(File.Exists(Path.Combine(stage, "python.exe")));
        Assert.IsTrue(File.Exists(Path.Combine(stage, "Lib/site-packages/pip/__main__.py")));
        Assert.IsTrue(File.Exists(Path.Combine(stage, "Notices/python-intel-openmp-2025.3.1-EULA.txt")));
        CollectionAssert.AreEqual(File.ReadAllBytes(Path.Combine(msvc, "msvcp140.dll")),
            File.ReadAllBytes(Path.Combine(stage, "msvcp140.dll")));
        Assert.AreEqual(178514752L, PythonBootstrapArtifacts.CpuDownloadSet().Sum(artifact => artifact.Bytes));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => PythonRuntimeStaging.PrepareCpuAsync(stage, cache, msvc, timeout.Token));
        await PythonRuntimeBundleVerifier.VerifyCpuAsync(stage, timeout.Token);
        await PythonRuntimeHealthProbe.VerifyCpuAsync(stage, timeout.Token);
        var extra = Path.Combine(stage, "unlisted-module.py");
        await File.WriteAllTextAsync(extra, "unexpected", timeout.Token);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => PythonRuntimeBundleVerifier.VerifyCpuAsync(stage, timeout.Token));
        File.Delete(extra);
        var altered = Path.Combine(stage, "Lib/site-packages/pip/__main__.py");
        var original = File.ReadAllBytes(altered);
        var changed = (byte[])original.Clone(); changed[0] ^= 1;
        try
        {
            await File.WriteAllBytesAsync(altered, changed, timeout.Token);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => PythonRuntimeBundleVerifier.VerifyCpuAsync(stage, timeout.Token));
        }
        finally { await File.WriteAllBytesAsync(altered, original, CancellationToken.None); }
        TestContext.WriteLine("Pinned full CPU stage retained; isolated imports and CPU computation passed: " + stage);
    }

    [TestMethod]
    public async Task OfficialPinnedWheelsExtractIntoIndependentStaging()
    {
        var source = Environment.GetEnvironmentVariable("AIHUB_PYTHON_WHEEL_CACHE");
        var stand = Environment.GetEnvironmentVariable("AIHUB_LLAMA_HARDWARE_STAND");
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(stand))
            Assert.Inconclusive("Explicit pinned wheel cache and isolated stand required.");
        var stage = Path.Combine(stand, "python-direct-wheels", Guid.NewGuid().ToString("N") + ".installing");
        Directory.CreateDirectory(stage);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        foreach (var artifact in PinnedPythonWheelSet.Load().Wheels)
            await PythonWheelExtractor.ExtractAsync(artifact, Path.Combine(source, artifact.FileName), stage, timeout.Token);
        Assert.IsTrue(File.Exists(Path.Combine(stage, "Lib/site-packages/torch/lib/torch_cpu.dll")));
        Assert.IsTrue(File.Exists(Path.Combine(stage, "Lib/site-packages/torch-2.10.0+cpu.dist-info/LICENSE")));
        Assert.IsTrue(File.Exists(Path.Combine(stage, "Lib/site-packages/PIL/Image.py")));
        Assert.IsFalse(Directory.EnumerateFiles(stage, "*cuda*.dll", SearchOption.AllDirectories).Any());
        Assert.IsFalse(Directory.EnumerateDirectories(stage, "__pycache__", SearchOption.AllDirectories).Any());
        TestContext.WriteLine("All official artifacts verified and staged, without executing package code: " + stage);
    }

    [TestMethod]
    [DataRow("../outside.py")]
    [DataRow("/absolute.py")]
    [DataRow("C:/drive.py")]
    [DataRow("module.py:stream")]
    [DataRow("module\\outside.py")]
    [DataRow("package.data/unknown/file")]
    [DataRow("package//file.py")]
    [DataRow("package/NUL.txt")]
    [DataRow("package/com1.py")]
    [DataRow("package/LPT\u00b2.txt")]
    [DataRow("package/file.py.")]
    [DataRow("package/file.py ")]
    [DataRow("package/file?.py")]
    [DataRow("package/file\u0000.py")]
    public void HostileArchivePathsAreRejected(string path)
    {
        Assert.ThrowsExactly<InvalidDataException>(() => PythonWheelExtractor.RelativeDestination(path));
    }

    [TestMethod]
    public async Task StagingPreservesModuleAndOriginalNoticesAndRejectsDigestChange()
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-wheel-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var archivePath = Path.Combine(root, "example.whl");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            foreach (var path in new[] { "package/__init__.py", "package-1.dist-info/LICENSE", "package-1.data/data/share/doc/notice.txt" })
            { using var writer = new StreamWriter(archive.CreateEntry(path).Open()); writer.Write("original content"); }
        var bytes = File.ReadAllBytes(archivePath);
        var artifact = new PinnedPythonArtifact("example", "1", "example.whl", bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), new Uri("https://files.pythonhosted.org/example.whl"));
        var stage = Path.Combine(root, "stage"); Directory.CreateDirectory(stage);
        try
        {
            await PythonWheelExtractor.ExtractAsync(artifact, archivePath, stage, CancellationToken.None);
            Assert.AreEqual("original content", File.ReadAllText(Path.Combine(stage, "Lib/site-packages/package-1.dist-info/LICENSE")));
            Assert.IsTrue(File.Exists(Path.Combine(stage, "share/doc/notice.txt")));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => PythonWheelExtractor.ExtractAsync(artifact, archivePath, stage, CancellationToken.None));
            File.AppendAllText(archivePath, "corrupted");
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => PythonWheelExtractor.ExtractAsync(artifact, archivePath, stage, CancellationToken.None));
        }
        finally { Directory.Delete(root, true); }
    }
}
