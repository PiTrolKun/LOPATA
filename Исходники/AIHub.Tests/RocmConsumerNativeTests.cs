using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Services;

namespace AIHub.Tests;

// CPU execution of the official AMD wheel verifies API compatibility, not AMD GPU support.
[TestClass]
[DoNotParallelize]
public sealed class RocmConsumerNativeTests
{
    public TestContext TestContext { get; set; } = null!;

    private static async Task<string> VerifiedRoot(CancellationToken token)
    {
        var root = Environment.GetEnvironmentVariable("AIHUB_PYTHON_STAGE_ROCM721");
        if (string.IsNullOrWhiteSpace(root)) Assert.Inconclusive("Explicit independently staged AMD runtime required.");
        await PythonRuntimeBundleVerifier.VerifyAsync(PythonRuntimeProfile.Rocm721, root, token);
        return root;
    }

    private async Task<string> Run(string root, string[] arguments, CancellationToken token)
    {
        var info = new ProcessStartInfo(Path.Combine(root, "python.exe"))
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        ManagedPythonLaunch.ScriptArguments(info, arguments);
        info.Environment["HF_HUB_OFFLINE"] = "1"; info.Environment["TRANSFORMERS_OFFLINE"] = "1";
        using var process = OwnedProcessRegistry.Shared.Start(info, "Test.RocmConsumer");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(token);
            Assert.AreEqual(0, process.ExitCode, await stderr);
            var output = await stdout; TestContext.WriteLine(output); return output;
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
        }
    }

    [TestMethod]
    public async Task OriginalAmdWheelProducesValidEmbeddingsOnCpu()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var root = await VerifiedRoot(timeout.Token);
        Assert.IsTrue(await GigaEmbeddingInstallation.ModelReadyAsync(timeout.Token, true));
        var folder = Path.Combine(Path.GetTempPath(), "lopata-rocm-giga-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var input = Path.Combine(folder, "input.json"); var output = Path.Combine(folder, "vectors.jsonl");
        File.WriteAllText(input, JsonSerializer.Serialize(new[] {
            new { source = "test", section = "one", text = "A small cat sleeps on the sofa." },
            new { source = "test", section = "two", text = "A small cat sleeps on the sofa." },
            new { source = "test", section = "three", text = "Volcanic ash covered the distant island." } }));
        var before = File.ReadAllBytes(input);
        await Run(root, [GigaEmbeddingInstallation.Script, "--model", GigaEmbeddingInstallation.ModelDirectory,
            "--input", input, "--output", output, "--device", "cpu"], timeout.Token);
        var vectors = File.ReadLines(output).Select(line => {
            using var row = JsonDocument.Parse(line);
            return row.RootElement.GetProperty("vector").EnumerateArray().Select(v => v.GetDouble()).ToArray(); }).ToArray();
        Assert.AreEqual(3, vectors.Length);
        foreach (var vector in vectors)
        {
            Assert.AreEqual(1024, vector.Length); Assert.IsTrue(vector.All(double.IsFinite));
            Assert.AreEqual(1d, vector.Sum(v => v * v), 0.02);
        }
        Assert.IsTrue(vectors[0].Zip(vectors[1]).Sum(p => Math.Abs(p.First - p.Second)) < 0.01);
        Assert.IsTrue(vectors[0].Zip(vectors[2]).Sum(p => Math.Abs(p.First - p.Second)) > 0.1);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(input));
        await PythonRuntimeBundleVerifier.VerifyAsync(PythonRuntimeProfile.Rocm721, root, timeout.Token);
    }

    [TestMethod]
    [DataRow("gliner")]
    [DataRow("nuextract")]
    public async Task OriginalAmdWheelRunsSpecialistsOnCpu(string mode)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        var root = await VerifiedRoot(timeout.Token);
        var source = Environment.GetEnvironmentVariable("AIHUB_JELLY_READONLY_STAND");
        if (string.IsNullOrWhiteSpace(source)) Assert.Inconclusive("Explicit readonly specialist weights required.");
        var manifest = LiteraryJellyInstallation.Manifest(mode);
        var model = Path.Combine(source, "models", manifest.GetProperty("directory").GetString()!);
        foreach (var file in manifest.GetProperty("files").EnumerateArray())
        {
            var sha = file.TryGetProperty("sha256", out var hash);
            Assert.IsTrue(await LiteraryArtifactDownload.ValidAsync(Path.Combine(model, file.GetProperty("filename").GetString()!),
                file.GetProperty("size").GetInt64(), sha ? hash.GetString()! : file.GetProperty("git_blob_sha1").GetString()!,
                sha ? "sha256" : "gitsha1", timeout.Token, true));
        }
        int pid;
        await using (var worker = new LiteraryJellyWorker(mode, model, Path.Combine(source, "deps", mode),
            (stage, value) => TestContext.WriteLine(stage + ": " + JsonSerializer.Serialize(value)), "cpu", Path.Combine(root, "python.exe")))
        {
            pid = worker.Id;
            var device = await worker.ReadAsync(timeout.Token);
            Assert.AreEqual("cpu", device.GetProperty("backend").GetString());
            Assert.AreEqual(PythonRuntimeProfile.Rocm721.TorchVersion(), device.GetProperty("torch").GetString());
            Assert.AreEqual("loaded", (await worker.CallAsync(new { action = "load" }, timeout.Token)).GetProperty("type").GetString());
            using var output = JsonDocument.Parse(await worker.ExtractAsync("Peter opened the door. Mary stayed in the garden.", timeout.Token));
            Assert.AreEqual(JsonValueKind.Array, output.RootElement.GetProperty("facts").ValueKind);
        }
        Assert.ThrowsExactly<ArgumentException>(() => Process.GetProcessById(pid));
        await PythonRuntimeBundleVerifier.VerifyAsync(PythonRuntimeProfile.Rocm721, root, timeout.Token);
    }

    [TestMethod]
    public async Task OriginalAmdWheelProducesSeparateSwinIrImageOnCpu()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var root = await VerifiedRoot(timeout.Token);
        var weights = Environment.GetEnvironmentVariable("AIHUB_SWINIR_HARDWARE_WEIGHTS");
        var dependencies = Environment.GetEnvironmentVariable("AIHUB_SWINIR_HARDWARE_DEPENDENCIES");
        if (string.IsNullOrWhiteSpace(weights) || string.IsNullOrWhiteSpace(dependencies)) Assert.Inconclusive("Verified SwinIR test inputs required.");
        var expected = ImageUtilityAiCatalog.Artifacts.Single(a => a.MethodId == "swinir").Files.Single(f => f.Purpose == "scale-2");
        await using (var file = File.OpenRead(weights)) Assert.AreEqual(expected.Sha256.ToUpperInvariant(), Convert.ToHexString(await SHA256.HashDataAsync(file, timeout.Token)));
        var folder = Path.Combine(Path.GetTempPath(), "lopata-rocm-swin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var input = Path.Combine(folder, "source.png"); var output = Path.Combine(folder, "result.png");
        await Run(root, ["-c", "from PIL import Image; import sys; Image.new('RGB',(8,8),(40,80,120)).save(sys.argv[1])", input], timeout.Token);
        var original = File.ReadAllBytes(input);
        await Run(root, [Path.Combine(AppContext.BaseDirectory, "Tools", "image-utility-swinir.py"), "--dependencies", dependencies,
            "--weights", weights, "--input", input, "--output", output, "--scale", "2", "--tile", "8", "--overlap", "0", "--device", "cpu"], timeout.Token);
        await Run(root, ["-c", "from PIL import Image; import sys; im=Image.open(sys.argv[1]); im.load(); assert im.size==(16,16)", output], timeout.Token);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(input));
        await PythonRuntimeBundleVerifier.VerifyAsync(PythonRuntimeProfile.Rocm721, root, timeout.Token);
    }
}
