using System.IO;
using System.Net.Http;
using System.Text;
using AIHub.Models;

namespace AIHub.Services;

public sealed partial class OmniLlamaRuntimeService
{
    private async Task<OmniTextGenerationResult> CompleteRequestAsync(string request,
        IProgress<ModelStreamChunk>? progress, Action<string>? responseReceived,
        Action<string>? diagnosticReceived, CancellationToken token)
    {
        var visible = false;
        var tracked = new CompletionProgress(chunk =>
        {
            if (chunk.Text.Length != 0) visible = true;
            progress?.Report(chunk);
        });
        Action<string> log = diagnosticReceived ?? (_ => { });
        using var operation = ApplicationInspector.EnsureOperation("omni-inference");
        return await NativeInferenceRecovery.ExecuteAsync(async () =>
        {
            token.ThrowIfCancellationRequested();
            Volatile.Write(ref _nativeHardwareFailed, 0);
            lock (_stderr) _stderr.Clear();
            var selection = _selectedRuntime;
            var attempt = await ApplicationInspector.BeginAttemptAsync("Omni llama-server", selection?.Bundle.Backend,
                selection is null ? null : selection.Device?.Id ?? "cpu", token, model: _profile.ArtifactId, stage: "generating");
            var requestId = Guid.NewGuid();
            attempt.LinkRequest(requestId);
            if (_process is { } process) attempt.Attach(process);
            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(Endpoint, "v1/chat/completions"))
                { Content = new StringContent(request, Encoding.UTF8, "application/json") };
                message.Headers.Add("X-LOPATA-Request-Id", requestId.ToString("N"));
                using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);
                await using var stream = await response.Content.ReadAsStreamAsync(token);
                if (!response.IsSuccessStatusCode)
                {
                    using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                    var buffer = new char[16 * 1024];
                    var length = 0;
                    while (length < buffer.Length)
                    {
                        var count = await reader.ReadAsync(buffer.AsMemory(length), token);
                        if (count == 0) break;
                        length += count;
                    }
                    var error = new string(buffer, 0, length);
                    responseReceived?.Invoke(error);
                    if (error.Contains("context", StringComparison.OrdinalIgnoreCase))
                        throw new ImageAnalysisContextExhaustedException(error);
                    throw new HttpRequestException($"Omni HTTP {(int)response.StatusCode}: {error}", null, response.StatusCode);
                }
                var result = await OmniLlamaProtocol.ReadAsync(stream, tracked, responseReceived, token, _profile);
                attempt.Succeed();
                return result;
            }
            catch (Exception error)
            {
                await attempt.FailAsync(error, token, ClassifyOmniFailure(token));
                throw;
            }
        }, () => _selectedRuntime?.UsesGpu == true, () => Volatile.Read(ref _nativeHardwareFailed) != 0,
            () => visible, async () => { Stop(); await AwaitProcessRetirementAsync(CancellationToken.None); },
            async () => { await PrepareAttemptAsync(log, null, token, true); }, log, token);
    }

    private sealed class CompletionProgress(Action<ModelStreamChunk> report) : IProgress<ModelStreamChunk>
    {
        public void Report(ModelStreamChunk value) => report(value);
    }
}
