using System.IO;
using System.Text.Json;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ManagedJellyConsumerNativeTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("gliner")]
    [DataRow("nuextract")]
    public async Task VerifiedSpecialistsRunOnManagedCpuAndAutomaticDevice(string mode)
    {
        var source = Environment.GetEnvironmentVariable("AIHUB_JELLY_READONLY_STAND");
        var isolated = Environment.GetEnvironmentVariable("LOPATA_UPDATE_STAND_ROOT");
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(isolated)
            || AppDataPaths.ProjectRoot is not null || !ManagedModelPathIdentity.SameDirectory(isolated, AppDataPaths.BaseDirectory))
            Assert.Inconclusive("Explicit readonly weights and isolated UPDATE_STAND are required.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        var manifest = LiteraryJellyInstallation.Manifest(mode);
        var model = Path.Combine(source, "models", manifest.GetProperty("directory").GetString()!);
        var dependencies = Path.Combine(source, "deps", mode);
        foreach (var file in manifest.GetProperty("files").EnumerateArray())
        {
            var sha256 = file.TryGetProperty("sha256", out var hash);
            Assert.IsTrue(await LiteraryArtifactDownload.ValidAsync(Path.Combine(model, file.GetProperty("filename").GetString()!),
                file.GetProperty("size").GetInt64(), sha256 ? hash.GetString()! : file.GetProperty("git_blob_sha1").GetString()!,
                sha256 ? "sha256" : "gitsha1", timeout.Token, true));
        }
        foreach (var policy in new[] { "cpu", "auto" })
        {
            int pid;
            await using (var worker = await LiteraryJellyWorker.CreateAsync(mode, model, dependencies,
                (stage, value) => TestContext.WriteLine(stage + ": " + JsonSerializer.Serialize(value)), policy, timeout.Token))
            {
                pid = worker.Id;
                var device = await worker.ReadAsync(timeout.Token);
                Assert.AreEqual("device", device.GetProperty("type").GetString());
                if (policy == "cpu") Assert.AreEqual("cpu", device.GetProperty("device").GetString());
                if (policy == "auto" && Environment.GetEnvironmentVariable("AIHUB_EXPECT_CUDA") == "1")
                    Assert.StartsWith("cuda:", device.GetProperty("device").GetString()!);
                Assert.AreEqual("loaded", (await worker.CallAsync(new { action = "load" }, timeout.Token)).GetProperty("type").GetString());
                using var output = JsonDocument.Parse(await worker.ExtractAsync("Peter opened the door. Mary stayed in the garden.", timeout.Token));
                Assert.AreEqual(JsonValueKind.Array, output.RootElement.GetProperty("facts").ValueKind);
            }
            Assert.ThrowsExactly<ArgumentException>(() => System.Diagnostics.Process.GetProcessById(pid));
        }
    }
}
