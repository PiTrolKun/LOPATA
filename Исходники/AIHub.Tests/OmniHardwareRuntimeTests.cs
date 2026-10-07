using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class OmniHardwareRuntimeTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void FullContextIncludesProjectorAndCpuVisionWorkspace()
    {
        var gib = LlamaDenseMemoryPolicy.GiB;
        var model = new LiteraryModelMemoryMetadata("qwen3", 32, 32768, 0, 5 * gib)
        { EmbeddingLength = 4096, HeadCount = 32, KvHeadCount = 8, KeyLength = 128, ValueLength = 128 };
        Assert.AreEqual(14 * gib, OmniRuntimeMemoryPolicy.GpuRequired(model, gib));
        Assert.AreEqual(18 * gib, OmniRuntimeMemoryPolicy.CpuDecision(model, gib, 32 * gib, 20 * gib).RequiredBytes);
        Assert.IsFalse(OmniRuntimeMemoryPolicy.CpuDecision(model, gib, 32 * gib, 17 * gib).Allowed);
        var args = OmniLlamaProtocol.Arguments("model", "projector", 12345);
        CollectionAssert.Contains(args, "--no-mmproj-offload");
        Assert.AreEqual("none", args[Array.IndexOf(args, "--device") + 1]);
        Assert.AreEqual("0", args[Array.IndexOf(args, "-ngl") + 1]);
        var gpu = OmniLlamaProtocol.Arguments("model", "projector", 12345, "Vulkan2", true);
        Assert.AreEqual("Vulkan2", gpu[Array.IndexOf(gpu, "--device") + 1]);
        CollectionAssert.Contains(gpu, "--mmproj-offload");
        Assert.AreEqual("32768", gpu[Array.IndexOf(gpu, "-c") + 1]);
    }

    [TestMethod]
    [DataRow("light", true)]
    [DataRow("light", false)]
    [DataRow("medium", true)]
    [DataRow("medium", false)]
    [DataRow("heavy", true)]
    [DataRow("heavy", false)]
    public async Task ManagedScenarioRecognizesImageWithFullContext(string bundle, bool cpu)
    {
        if (Environment.GetEnvironmentVariable("AIHUB_OMNI_MANAGED_NATIVE") != "1")
            Assert.Inconclusive("Explicit real model/projector verification only.");
        var profile = OmniLlamaProfile.ForBundle(bundle)!;
        var folder = Path.Combine(Environment.GetEnvironmentVariable("AIHUB_LLAMA_HARDWARE_STAND")!,
            "omni-native-tests", bundle + (cpu ? "-cpu-" : "-gpu-") + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var log = Path.Combine(folder, "runtime.log");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        using var runtime = new OmniLlamaRuntimeService(new ManagedModelLibraryStore(), profile, cpu);
        try
        {
            await runtime.PrepareAsync(line => { lock (log) File.AppendAllText(log, line + Environment.NewLine); }, null, timeout.Token);
            TestContext.WriteLine(runtime.CurrentExecutable + "; " + runtime.DeviceMapJson);
            Assert.IsTrue(runtime.CurrentExecutable!.Contains(cpu ? "runtime.llama.cpu" : "runtime.llama.vulkan", StringComparison.Ordinal));
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(12) };
            using var slots = JsonDocument.Parse(await client.GetStringAsync(new Uri(runtime.Endpoint, "slots"), timeout.Token));
            Assert.AreEqual(32768, slots.RootElement[0].GetProperty("n_ctx").GetInt32());
            // A real encoded image, no text leaking the expected colour into the prompt.
            var pixels = new byte[64 * 64 * 3];
            for (var i = 0; i < pixels.Length; i += 3) pixels[i] = 255;
            var bitmap = BitmapSource.Create(64, 64, 96, 96, PixelFormats.Rgb24, null, pixels, 64 * 3);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var image = new MemoryStream(); encoder.Save(image);
            File.WriteAllBytes(Path.Combine(folder, "input.png"), image.ToArray());
            var content = new object[] {
                new { type = "image_url", image_url = new { url = "data:image/png;base64," + Convert.ToBase64String(image.ToArray()) } },
                new { type = "text", text = "What colour fills this image? Reply with one colour word." } };
            using var response = await client.PostAsJsonAsync(new Uri(runtime.Endpoint, "v1/chat/completions"),
                new { messages = new[] { new { role = "user", content } }, max_tokens = 24, temperature = 0,
                    stream = false, chat_template_kwargs = new { enable_thinking = false } }, timeout.Token);
            var text = await response.Content.ReadAsStringAsync(timeout.Token);
            await File.WriteAllTextAsync(Path.Combine(folder, "response.json"), text, timeout.Token);
            response.EnsureSuccessStatusCode();
            using var reply = JsonDocument.Parse(text);
            var answer = reply.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()!;
            TestContext.WriteLine(answer);
            Assert.IsTrue(answer.Contains("red", StringComparison.OrdinalIgnoreCase), answer);
        }
        finally { runtime.Stop(); await runtime.AwaitProcessRetirementAsync(CancellationToken.None); }
    }
}
