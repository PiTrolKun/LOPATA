using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
[DoNotParallelize]
public sealed class LlamaServerInferenceNativeTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task PrivateFinancialRequestRetainsPolicyAndCompletesAfterGpuRetirement()
    {
        var model = RequireModel(); model.Format = "gguf";
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var handler = new FaultHandler(false);
        using var runtime = new FinancialModelRuntime(new(new UserProfileStore(), new IpLocationService()), handler);
        var previousGate = ComponentLicenseGate.ConfirmAsync;
        ComponentLicenseGate.ConfirmAsync = (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; };
        try
        {
            var text = await runtime.GenerateAsync(model, "Reply only with the requested word. Do not explain. /no_think",
                "Name the capital of France.", 128, timeout.Token);
            Assert.IsTrue(text.Contains("Paris", StringComparison.OrdinalIgnoreCase), text);
            Assert.AreEqual(2, handler.Requests.Count);
            Assert.AreEqual(handler.Requests[0], handler.Requests[1]);
            using var payload = JsonDocument.Parse(handler.Requests[1]);
            Assert.IsFalse(payload.RootElement.GetProperty("cache_prompt").GetBoolean());
            Assert.AreEqual(0.15, payload.RootElement.GetProperty("temperature").GetDouble());
            Assert.IsFalse(payload.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
            Assert.AreEqual(2, payload.RootElement.GetProperty("messages").GetArrayLength());
            Assert.IsTrue(handler.GpuPids.Count > 0);
            Assert.IsFalse(OwnedProcessRegistry.Shared.GetSnapshot().Any(process => handler.GpuPids.Contains(process.Pid)), "Old GPU worker survived financial recovery.");
            TestContext.WriteLine(text);
        }
        finally { runtime.Stop(); ComponentLicenseGate.ConfirmAsync = previousGate; }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ActualGpuWorkerIsRetiredAndIdenticalRequestCompletesOnFreshCpu(bool streaming)
    {
        var model = RequireModel();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var handler = new FaultHandler(false);
        using var runtime = new LlamaServerRuntimeService(new(new UserProfileStore(), new IpLocationService()), false, handler);
        var diagnostics = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var chunks = new List<ModelStreamChunk>();
        var previousGate = ComponentLicenseGate.ConfirmAsync;
        ComponentLicenseGate.ConfirmAsync = (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; };
        try
        {
            await runtime.PrepareAsync(model, diagnostics.Enqueue, timeout.Token);
            Assert.IsTrue(runtime.ExpectedExecutablePath.Contains("runtime.llama.vulkan", StringComparison.Ordinal));
            var text = await runtime.GenerateTextAsync(model, "Reply only with the requested word. Do not explain.",
                "Name the capital of France.", 128, 0, diagnostics.Enqueue, timeout.Token,
                streaming ? new InlineProgress(chunks.Add) : null);
            Assert.IsTrue(text.Contains("Paris", StringComparison.OrdinalIgnoreCase), text);
            Assert.IsTrue(runtime.ExpectedExecutablePath.Contains("runtime.llama.cpu", StringComparison.Ordinal));
            Assert.AreEqual(2, handler.Requests.Count);
            Assert.AreEqual(handler.Requests[0], handler.Requests[1]);
            AssertRetired(diagnostics);
            if (streaming) Assert.IsTrue(chunks.Last().IsComplete);
            TestContext.WriteLine(text);
        }
        finally
        {
            runtime.Stop();
            ComponentLicenseGate.ConfirmAsync = previousGate;
            TestContext.WriteLine(string.Join(Environment.NewLine, diagnostics));
        }
    }

    [TestMethod]
    public async Task PartialStreamFailureRetiresGpuWithoutCpuRetryOrCompletion()
    {
        var model = RequireModel();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var handler = new FaultHandler(true);
        using var runtime = new LlamaServerRuntimeService(new(new UserProfileStore(), new IpLocationService()), false, handler);
        var diagnostics = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var chunks = new List<ModelStreamChunk>();
        var previousGate = ComponentLicenseGate.ConfirmAsync;
        ComponentLicenseGate.ConfirmAsync = (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; };
        try
        {
            await Assert.ThrowsAsync<IOException>(() => runtime.GenerateTextAsync(model, "Reply briefly.", "Say OK.",
                128, 0, diagnostics.Enqueue, timeout.Token, new InlineProgress(chunks.Add)));
            Assert.AreEqual(1, handler.Requests.Count);
            Assert.AreEqual("partial", string.Concat(chunks.Select(chunk => chunk.Text)));
            Assert.IsFalse(chunks.Any(chunk => chunk.IsComplete));
            AssertRetired(diagnostics);
            Assert.AreEqual(string.Empty, runtime.Endpoint);
        }
        finally { runtime.Stop(); ComponentLicenseGate.ConfirmAsync = previousGate; }
    }

    [TestMethod]
    public async Task CancelledRequestRetiresItsActualWorkerWithoutRetry()
    {
        var model = RequireModel();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var handler = new FaultHandler(false, cancellation);
        using var runtime = new LlamaServerRuntimeService(new(new UserProfileStore(), new IpLocationService()), false, handler);
        var diagnostics = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var previousGate = ComponentLicenseGate.ConfirmAsync;
        ComponentLicenseGate.ConfirmAsync = (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; };
        try
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => runtime.GenerateTextAsync(model, "Reply briefly.", "Say OK.",
                128, 0, diagnostics.Enqueue, cancellation.Token));
            Assert.AreEqual(1, handler.Requests.Count);
            AssertRetired(diagnostics);
            Assert.AreEqual(string.Empty, runtime.Endpoint);
        }
        finally { runtime.Stop(); ComponentLicenseGate.ConfirmAsync = previousGate; }
    }

    private static DebugModelInfo RequireModel()
    {
        var path = Environment.GetEnvironmentVariable("AIHUB_CORE_INFERENCE_NATIVE_MODEL");
        if (string.IsNullOrWhiteSpace(path)) Assert.Inconclusive("Explicit verified native GGUF and CPU/Vulkan components required.");
        Assert.IsTrue(File.Exists(path));
        return new DebugModelInfo { Path = path, Name = "Native hardware inference test" };
    }

    private static void AssertRetired(IEnumerable<string> diagnostics)
    {
        var launch = diagnostics.First(line => line.Contains("pid=", StringComparison.Ordinal));
        var pid = int.Parse(System.Text.RegularExpressions.Regex.Match(launch, @"pid=(\d+)").Groups[1].Value);
        try { using var process = Process.GetProcessById(pid); Assert.IsTrue(process.HasExited, "Old GPU process survived fallback."); }
        catch (ArgumentException) { }
    }

    private sealed class InlineProgress(Action<ModelStreamChunk> report) : IProgress<ModelStreamChunk>
    {
        public void Report(ModelStreamChunk value) => report(value);
    }

    private sealed class FaultHandler(bool partial, CancellationTokenSource? cancel = null) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _actual = new(new HttpClientHandler());
        public List<string> Requests { get; } = [];
        public List<int> GpuPids { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/v1/chat/completions")
            {
                var body = await request.Content!.ReadAsStringAsync(token);
                Requests.Add(body);
                if (cancel is not null) { cancel.Cancel(); token.ThrowIfCancellationRequested(); }
                using var document = JsonDocument.Parse(body);
                Assert.IsTrue(document.RootElement.GetProperty("max_tokens").GetInt32() == 128);
                if (Requests.Count == 1)
                {
                    GpuPids.AddRange(OwnedProcessRegistry.Shared.GetSnapshot().Where(process => process.Component == "LlamaServerRuntimeService").Select(process => process.Pid));
                    return partial
                        ? new(HttpStatusCode.OK) { Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\ndata: {\"error\":{\"message\":\"VK_ERROR_OUT_OF_DEVICE_MEMORY\"}}\n", Encoding.UTF8, "text/event-stream") }
                        : new(HttpStatusCode.InternalServerError) { Content = new StringContent("VK_ERROR_OUT_OF_DEVICE_MEMORY") };
                }
            }
            return await _actual.SendAsync(request, token);
        }
        protected override void Dispose(bool disposing) { if (disposing) _actual.Dispose(); base.Dispose(disposing); }
    }
}
