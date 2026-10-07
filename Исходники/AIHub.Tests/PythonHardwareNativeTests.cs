using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class PythonHardwareNativeTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("cpu")]
    [DataRow("auto")]
    public async Task ProductionGigaPipelinePreservesInputAndProducesValidCheckpoint(string device)
    {
        var stand = Environment.GetEnvironmentVariable("AIHUB_LLAMA_HARDWARE_STAND");
        if (string.IsNullOrWhiteSpace(stand)) Assert.Inconclusive("Explicit native pipeline stand required.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await ComponentLicenseGate.EnsureAsync([GigaEmbeddingInstallation.LicenseId, GigaEmbeddingInstallation.RuntimeLicenseId], timeout.Token);
        Assert.IsTrue(await GigaEmbeddingInstallation.ModelReadyAsync(timeout.Token));
        var folder = Path.Combine(stand, "giga-pipeline-tests", device + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var input = Path.Combine(folder, "input.json"); var output = Path.Combine(folder, "vectors.jsonl");
        await File.WriteAllTextAsync(input, JsonSerializer.Serialize(new[] {
            new { source = "native-test", section = "one", text = "Peter opened the door and Mary stayed in the garden." } }), timeout.Token);
        var before = File.ReadAllBytes(input);
        var events = new List<LiteraryPreparationProgress>();
        await new GigaSourceEmbedding(device).EmbedAsync(input, output,
            new InlineProgress<LiteraryPreparationProgress>(value => { lock (events) events.Add(value); }), timeout.Token);
        using var record = JsonDocument.Parse(File.ReadAllLines(output).Single());
        var vector = record.RootElement.GetProperty("vector").EnumerateArray().Select(value => value.GetDouble()).ToArray();
        Assert.AreEqual(1024, vector.Length);
        Assert.IsTrue(vector.All(double.IsFinite));
        Assert.AreEqual(1d, vector.Sum(value => value * value), 0.02);
        Assert.IsTrue(events.Any(value => value.Stage == "Complete"));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(input));
        TestContext.WriteLine(JsonSerializer.Serialize(events));
    }

    [TestMethod]
    [DataRow("gliner")]
    [DataRow("nuextract")]
    public async Task CpuOnlyLibrariesRunSpecialistExtractors(string mode)
    {
        var root = Environment.GetEnvironmentVariable("AIHUB_PYTHON_CPU_CANDIDATE");
        if (string.IsNullOrWhiteSpace(root)) Assert.Inconclusive("Explicit CPU-only candidate verification.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        await ComponentLicenseGate.EnsureAsync(LiteraryJellyInstallation.Licenses, timeout.Token);
        var model = await LiteraryJellyInstallation.FindModelAsync(mode, timeout.Token);
        var deps = await LiteraryJellyInstallation.FindDependenciesAsync(mode, timeout.Token);
        Assert.IsNotNull(model); Assert.IsNotNull(deps);
        var folder = Path.Combine(Environment.GetEnvironmentVariable("AIHUB_LLAMA_HARDWARE_STAND")!,
            "jelly-cpu-only-tests", mode + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var log = new StringBuilder();
        int pid;
        await using (var worker = new LiteraryJellyWorker(mode, model, deps,
            (stage, value) => { lock (log) log.AppendLine(stage + ": " + JsonSerializer.Serialize(value)); },
            "cpu", Path.Combine(root, "python.exe")))
        {
            pid = worker.Id;
            try
            {
                var device = await worker.ReadAsync(timeout.Token);
                Assert.AreEqual("cpu", device.GetProperty("backend").GetString());
                Assert.AreEqual("2.10.0+cpu", device.GetProperty("torch").GetString());
                var loaded = await worker.CallAsync(new { action = "load" }, timeout.Token);
                Assert.AreEqual("loaded", loaded.GetProperty("type").GetString());
                using var result = JsonDocument.Parse(await worker.ExtractAsync("Peter opened the door. Mary stayed in the garden.", timeout.Token));
                Assert.AreEqual(JsonValueKind.Array, result.RootElement.GetProperty("facts").ValueKind);
                TestContext.WriteLine(result.RootElement.ToString());
            }
            finally { lock (log) File.WriteAllText(Path.Combine(folder, "runtime.log"), log.ToString()); }
        }
        Assert.ThrowsExactly<ArgumentException>(() => Process.GetProcessById(pid));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CpuOnlyAndCudaLibrariesProduceValidNonconstantEmbeddings(bool gpu)
    {
        var root = Environment.GetEnvironmentVariable("AIHUB_PYTHON_CPU_CANDIDATE");
        if (string.IsNullOrWhiteSpace(root)) Assert.Inconclusive("Explicit CPU-only runtime candidate verification.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        await ComponentLicenseGate.EnsureAsync([GigaEmbeddingInstallation.LicenseId, GigaEmbeddingInstallation.RuntimeLicenseId], timeout.Token);
        Assert.IsTrue(await GigaEmbeddingInstallation.ModelReadyAsync(timeout.Token));
        var python = gpu ? Path.Combine(GigaEmbeddingInstallation.LegacyRoot, "python.exe") : Path.Combine(root, "python.exe");
        var folder = Path.Combine(Environment.GetEnvironmentVariable("AIHUB_LLAMA_HARDWARE_STAND")!,
            "giga-native-tests", (gpu ? "gpu-" : "cpu-") + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var input = Path.Combine(folder, "input.json"); var output = Path.Combine(folder, "vectors.jsonl");
        await File.WriteAllTextAsync(input, JsonSerializer.Serialize(new[] {
            new { source = "test", section = "one", text = "A small cat sleeps on the sofa." },
            new { source = "test", section = "two", text = "A small cat sleeps on the sofa." },
            new { source = "test", section = "three", text = "Volcanic ash covered the distant island." } }), timeout.Token);
        var before = File.ReadAllBytes(input);
        var info = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(python)!, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in new[] { "-B", Path.Combine(AppContext.BaseDirectory, "Tools", "giga_embeddings.py"),
            "--model", GigaEmbeddingInstallation.ModelDirectory, "--input", input, "--output", output,
            "--device", gpu ? "cuda" : "cpu" }) info.ArgumentList.Add(arg);
        info.Environment["PYTHONUTF8"] = "1"; info.Environment["HF_HUB_OFFLINE"] = "1";
        info.Environment["TRANSFORMERS_OFFLINE"] = "1";
        using var process = OwnedProcessRegistry.Shared.Start(info, "Giga.NativeHardwareTest");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var diagnostic = await stderr; var events = await stdout;
            await File.WriteAllTextAsync(Path.Combine(folder, "runtime.log"), events + "\n" + diagnostic, timeout.Token);
            Assert.AreEqual(0, process.ExitCode, diagnostic);
            TestContext.WriteLine(events);
            Assert.IsTrue(events.Contains(gpu ? "cuda:" : "\"device\": \"cpu\"", StringComparison.Ordinal));
            var vectors = File.ReadLines(output).Select(line => {
                using var row = JsonDocument.Parse(line);
                return row.RootElement.GetProperty("vector").EnumerateArray().Select(v => v.GetDouble()).ToArray(); }).ToArray();
            Assert.AreEqual(3, vectors.Length);
            foreach (var vector in vectors)
            {
                Assert.AreEqual(1024, vector.Length);
                Assert.IsTrue(vector.All(double.IsFinite));
                Assert.AreEqual(1d, vector.Sum(v => v * v), 0.02);
            }
            Assert.IsTrue(vectors[0].Zip(vectors[1]).Sum(pair => Math.Abs(pair.First - pair.Second)) < 0.01);
            Assert.IsTrue(vectors[0].Zip(vectors[2]).Sum(pair => Math.Abs(pair.First - pair.Second)) > 0.1);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(input));
        }
        finally { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); await Task.WhenAll(stdout, stderr); }
    }
}
