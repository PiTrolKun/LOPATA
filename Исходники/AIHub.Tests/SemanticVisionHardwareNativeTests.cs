using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
[DoNotParallelize]
public sealed class SemanticVisionHardwareNativeTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task OriginalSemanticModelProducesGroundedResultWithOwnedWorkerRetirement(bool cpu, bool fault)
    {
        if (Environment.GetEnvironmentVariable("AIHUB_SEMANTIC_NATIVE") != "1")
            Assert.Inconclusive("Explicit original installed semantic model/projector required.");
        var directory = Path.Combine(Path.GetTempPath(), "lopata-semantic-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "source.png");
        using (var bitmap = new SkiaSharp.SKBitmap(32, 32))
        {
            bitmap.Erase(SkiaSharp.SKColors.Red);
            using var png = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(path, png.ToArray());
        }
        var original = File.ReadAllBytes(path);
        var manifest = new SessionFileManifest { Files = [new SessionFileReference { Id = "red", SourcePath = path,
            DisplayName = "source.png", Extension = ".png", Category = SessionFileCategories.Image,
            IsAvailable = true, SizeBytes = original.Length }] };
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var handler = new HardwareFaultHandler(fault);
        var service = new SemanticImageToolService(cpu, handler);
        var previousGate = ComponentLicenseGate.ConfirmAsync;
        ComponentLicenseGate.ConfirmAsync = (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; };
        try
        {
            var result = await service.DescribeAsync(manifest, "red", "Name the predominant color. Reply briefly.", "en", timeout.Token);
            using var document = JsonDocument.Parse(result);
            Assert.IsTrue(document.RootElement.GetProperty("success").GetBoolean());
            Assert.IsTrue(document.RootElement.GetProperty("description").GetString()!.Contains("red", StringComparison.OrdinalIgnoreCase), result);
            Assert.AreEqual(fault ? 2 : 1, handler.Requests.Count);
            if (fault) Assert.AreEqual(handler.Requests[0], handler.Requests[1]);
            CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
            Assert.IsTrue(handler.Pids.Count > 0);
            Assert.IsFalse(OwnedProcessRegistry.Shared.GetSnapshot().Any(process => handler.Pids.Contains(process.Pid)));
            TestContext.WriteLine(result);
        }
        finally { ComponentLicenseGate.ConfirmAsync = previousGate; }
    }

    private sealed class HardwareFaultHandler(bool fault) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _actual = new(new HttpClientHandler());
        public List<string> Requests { get; } = [];
        public List<int> Pids { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(token));
            Pids.AddRange(OwnedProcessRegistry.Shared.GetSnapshot().Where(process => process.Component == "SemanticImageToolService").Select(process => process.Pid));
            if (fault && Requests.Count == 1)
                return new(HttpStatusCode.InternalServerError) { Content = new StringContent("VK_ERROR_OUT_OF_DEVICE_MEMORY") };
            return await _actual.SendAsync(request, token);
        }
        protected override void Dispose(bool disposing) { if (disposing) _actual.Dispose(); base.Dispose(disposing); }
    }
}
