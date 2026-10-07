using System.Net;
using System.Net.Http;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
[DoNotParallelize]
public sealed class OmniInferenceHardwareNativeTests
{
    [TestMethod]
    public async Task CompletionHardwareFailureRetiresGpuAndRepeatsUnchangedRequestOnCpu()
    {
        if (Environment.GetEnvironmentVariable("AIHUB_OMNI_MANAGED_NATIVE") != "1")
            Assert.Inconclusive("Explicit original installed Omni model and CPU/Vulkan runtimes required.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var handler = new CompletionFault();
        using var runtime = new OmniLlamaRuntimeService(new ManagedModelLibraryStore(), OmniLlamaProfile.Alpha, false, handler);
        var previous = ComponentLicenseGate.ConfirmAsync;
        ComponentLicenseGate.ConfirmAsync = (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; };
        try
        {
            await runtime.PrepareAsync(_ => { }, null, timeout.Token);
            Assert.IsTrue(runtime.CurrentExecutable!.Contains("runtime.llama.vulkan", StringComparison.Ordinal));
            var result = await runtime.GenerateAsync("compose", string.Empty,
                [new ImageAnalysisHiddenMessage { Role = "user", Content = "What is the capital of France? Reply only with the city name." }],
                null, timeout.Token);
            Assert.IsTrue(result.Content.Contains("Paris", StringComparison.OrdinalIgnoreCase), result.Content);
            Assert.AreEqual(2, handler.Requests.Count);
            Assert.AreEqual(handler.Requests[0], handler.Requests[1]);
            Assert.IsTrue(runtime.CurrentExecutable!.Contains("runtime.llama.cpu", StringComparison.Ordinal));
            Assert.IsFalse(OwnedProcessRegistry.Shared.GetSnapshot().Any(process => handler.FirstPids.Contains(process.Pid)));
        }
        finally
        {
            runtime.Stop();
            await runtime.AwaitProcessRetirementAsync(CancellationToken.None);
            ComponentLicenseGate.ConfirmAsync = previous;
        }
    }

    private sealed class CompletionFault : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _actual = new(new HttpClientHandler());
        public List<string> Requests { get; } = [];
        public HashSet<int> FirstPids { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/v1/chat/completions")
            {
                Requests.Add(await request.Content!.ReadAsStringAsync(token));
                if (Requests.Count == 1)
                {
                    FirstPids.UnionWith(OwnedProcessRegistry.Shared.GetSnapshot().Where(p => p.Component == "OmniLlamaRuntimeService").Select(p => p.Pid));
                    return new(HttpStatusCode.InternalServerError) { Content = new StringContent("VK_ERROR_OUT_OF_DEVICE_MEMORY") };
                }
            }
            return await _actual.SendAsync(request, token);
        }
        protected override void Dispose(bool disposing) { if (disposing) _actual.Dispose(); base.Dispose(disposing); }
    }
}
