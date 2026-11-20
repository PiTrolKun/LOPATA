using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Owns the pinned sd.cpp server while an image workspace is active.</summary>
internal sealed class ImageGenerationResident
{
    internal const string Interrupted = "Generation.Qwen.ResidentInterrupted";

    // The older visual worker persisted the transport exception type rather than a recovery reason.
    internal static bool RequiresStandalone(string? detail) => detail is Interrupted or nameof(HttpRequestException);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Session? _session;
    private volatile bool _warm;

    internal ImageGenerationResident() =>
        ApplicationBackgroundOperations.RegisterModel(this, owner => owner.RetireAsync());

    internal async Task RetireAsync()
    {
        await _gate.WaitAsync();
        try { await CloseSessionAsync(); }
        finally { _gate.Release(); }
    }

    internal async Task WarmAsync(ImageGenerationRequest request, IReadOnlyList<ManagedModelArtifactCard> cards,
        SdRuntimeSelection selection, CancellationToken token)
    {
        var model = ImageGenerationCatalog.Get(request.ModelId);
        var command = ImageGenerationNativeWorker.ManagedCommand(request, 0, cards, "", "", selection, server: true);
        await _gate.WaitAsync(token);
        try
        {
            var session = await ReadySessionAsync(command, model.RequiresGpu, token);
            if (_warm) return;
            // A server that answers capabilities can still defer loading model weights to its first inference.
            var (width, height) = WarmupSize(model);
            var sample = request with { Width = width, Height = height };
            if (session.Rejected || !await session.DrawAsync(sample, 0, null, null, token, warmup: true))
                throw new BackgroundOperationFailedException("Generation.WarmFailed");
            _warm = true;
            await SaveWarmupLogAsync(request, selection, session.Process.Id, "ready", session.ReadLog());
            OwnedProcessRegistry.Log("resident_warmed", "ImageGeneration", session.Process.Id, request.ModelId);
        }
        catch (Exception error)
        {
            if (_session is { } failed)
            {
                var pid = failed.Process.Id;
                var exit = failed.Process.HasExited ? $"0x{failed.Process.ExitCode:X8}" : "running before shutdown";
                try { await CloseSessionAsync(); }
                finally
                {
                    await SaveWarmupLogAsync(request, selection, pid, $"{error.GetType().Name}; {exit}", failed.ReadLog());
                    OwnedProcessRegistry.Log("resident_warmup_ended", "ImageGeneration", pid, request.ModelId);
                }
            }
            throw;
        }
        finally { _gate.Release(); }
    }

    internal static (int Width, int Height) WarmupSize(ImageGenerationModel model) =>
        model.RequiresGpu || model.Id == MiniMaxImageRuntime.ModelId
            ? (model.DefaultWidth, model.DefaultHeight) : (256, 256);

    internal static bool RejectsPlacement(bool requireGpu, ImageGenerationGpuPlacement placement) =>
        requireGpu && placement is (ImageGenerationGpuPlacement.GpuUnavailable or ImageGenerationGpuPlacement.MemoryUnavailable);

    private static async Task SaveWarmupLogAsync(ImageGenerationRequest request, SdRuntimeSelection selection,
        int pid, string outcome, (string Log, ImageGenerationGpuPlacement Placement) diagnostic)
    {
        try
        {
            var path = Path.Combine(request.SessionDirectory, request.Id + ".warmup.log");
            Lopata.Updates.SafeUpdatePath.RejectLinks(path);
            var (width, height) = WarmupSize(ImageGenerationCatalog.Get(request.ModelId));
            await File.WriteAllTextAsync(path, $"sd-server pid={pid}; model={request.ModelId}; size={width}x{height}; "
                + $"backend={selection.Revision}/{selection.Backend}; outcome={outcome}; placement={diagnostic.Placement}\n{diagnostic.Log}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        { OwnedProcessRegistry.Log("resident_diagnostic_failed", "ImageGeneration", pid, error.GetType().Name); }
    }

    internal async Task<(int ExitCode, string Log, ImageGenerationGpuPlacement Placement)> GenerateAsync(
        ImageGenerationRequest request, int index, IReadOnlyList<ManagedModelArtifactCard> cards, string output,
        SdRuntimeSelection selection, IProgress<BitmapSource>? preview, CancellationToken token, Action<bool>? starting = null)
    {
        var command = ImageGenerationNativeWorker.ManagedCommand(request, index, cards, "", output, selection, server: true);
        await _gate.WaitAsync(token);
        var cold = false;
        try
        {
            cold = starting is not null && (!_warm || _session is not { } previous || !previous.Matches(command));
            if (cold) starting!(true);
            var session = await ReadySessionAsync(command, ImageGenerationCatalog.Get(request.ModelId).RequiresGpu, token);
            var success = !session.Rejected && await session.DrawAsync(request, index, output, preview, token);
            if (success) _warm = true;
            if (!success && session.Process.HasExited && !session.Rejected)
                throw await RecordInterruptionAsync(request, index, session, null);
            var (log, placement) = session.ReadLog();
            var receipt = $"Backend: {selection.Revision}; engineSet={selection.EngineSetId}; version={selection.Version}; "
                + $"runtime={selection.ComponentId}; backend={selection.Backend}; executable={command.FileName}\n"
                + $"sd-server pid={session.Process.Id}; placement={placement}; model={request.ModelId}; "
                + $"size={request.Width}x{request.Height}; seed={request.Seeds[index]}\n{log}";
            if (!success) await CloseSessionAsync();
            return (success ? 0 : 1, receipt, placement);
        }
        catch (Exception error) when (!token.IsCancellationRequested && _session is { } failed
            && (error is HttpRequestException { StatusCode: null } or OperationCanceledException))
        {
            var interruption = await RecordInterruptionAsync(request, index, failed, error);
            token.ThrowIfCancellationRequested();
            throw interruption;
        }
        catch
        {
            await CloseSessionAsync();
            throw;
        }
        finally
        {
            _gate.Release();
            if (cold) starting!(false);
        }
    }

    private async Task<Session> ReadySessionAsync(ProcessStartInfo command, bool requireGpu, CancellationToken token)
    {
        if (_session is { } current && current.Matches(command)) return current;
        await CloseSessionAsync();
        token.ThrowIfCancellationRequested();
        _session = new(command, requireGpu);
        await _session.WaitUntilReadyAsync(token);
        return _session;
    }

    private async Task<BackgroundOperationWaitingException> RecordInterruptionAsync(
        ImageGenerationRequest request, int index, Session failed, Exception? error)
    {
        var process = failed.Process;
        var pid = process.Id;
        var exit = process.HasExited ? $"0x{process.ExitCode:X8}" : "still running at transport failure";
        await CloseSessionAsync();
        var (log, placement) = failed.ReadLog();
        OwnedProcessRegistry.Log("resident_interrupted", "ImageGeneration", pid, $"exit={exit}; transport={error?.GetType().Name}");
        try
        {
            var file = Path.Combine(request.SessionDirectory, $"{request.Id}_{index}.resident-error.log");
            await File.WriteAllTextAsync(file, $"sd-server pid={pid}; exit={exit}; placement={placement}\n{error?.GetType().Name}\n{log}");
        }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException)
        {
            OwnedProcessRegistry.Log("resident_diagnostic_failed", "ImageGeneration", pid, logError.GetType().Name);
        }
        return new(Interrupted);
    }

    private async Task CloseSessionAsync()
    {
        _warm = false;
        if (_session is not { } session) return;
        await session.StopAsync();
        _session = null;
    }

    private sealed class Session
    {
        private const string Api = "sdcpp/v1/";
        private static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(15) };
        private readonly string _executable;
        private readonly string[] _arguments;
        private readonly bool _requireGpu;
        private readonly Uri _endpoint;
        private readonly Task _logs;
        private readonly StringBuilder _log = new();
        private ImageGenerationGpuPlacement _placement;
        internal Process Process { get; }

        internal Session(ProcessStartInfo command, bool requireGpu)
        {
            _executable = command.FileName;
            _arguments = command.ArgumentList.ToArray();
            _requireGpu = requireGpu;
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            _endpoint = new UriBuilder(Uri.UriSchemeHttp, IPAddress.Loopback.ToString(), port).Uri;
            command.ArgumentList.Add("--listen-ip");
            command.ArgumentList.Add(IPAddress.Loopback.ToString());
            command.ArgumentList.Add("--listen-port");
            command.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
            Process = OwnedProcessRegistry.Shared.Start(command, "ImageGeneration");
            _logs = Task.WhenAll(DrainAsync(Process.StandardOutput), DrainAsync(Process.StandardError));
        }

        internal bool Matches(ProcessStartInfo command) => !Process.HasExited
            && _executable == command.FileName && _arguments.SequenceEqual(command.ArgumentList);

        internal bool Rejected
        {
            get
            {
                lock (_log) return RejectsPlacement(_requireGpu, _placement);
            }
        }

        internal async Task WaitUntilReadyAsync(CancellationToken token)
        {
            while (!Process.HasExited && !Rejected)
            {
                try
                {
                    using var response = await Http.GetAsync(Address("capabilities"), token);
                    if (response.IsSuccessStatusCode) return;
                }
                catch (HttpRequestException) { }
                await Task.Delay(200, token);
            }
            if (Process.HasExited) await _logs;
        }

        internal async Task<bool> DrawAsync(ImageGenerationRequest request, int index,
            string? output, IProgress<BitmapSource>? preview, CancellationToken token, bool warmup = false)
        {
            if (Process.HasExited) return false;
            var references = new List<string>(request.ReferenceImages.Length);
            foreach (var path in request.ReferenceImages)
                references.Add(Convert.ToBase64String(await File.ReadAllBytesAsync(path, token)));
            var payload = new Dictionary<string, object>
            {
                ["prompt"] = request.Prompt, ["width"] = request.Width, ["height"] = request.Height, ["seed"] = request.Seeds[index],
                ["output_format"] = "png", ["embed_image_metadata"] = false,
                ["ref_images"] = references, ["ref_image_args"] = ImageGenerationReferences.NativeOptions(request),
                ["preview"] = preview is null ? "none" : "proj", ["preview_interval"] = 1
            };
            // A fixed sigma schedule defines the number of steps for Turbo and MiniMax.
            if (warmup && ImageGenerationCatalog.Get(request.ModelId).Sigmas is null)
                payload["sample_params"] = new { sample_steps = 1 };
            using var response = await Http.PostAsJsonAsync(Address("img_gen"), payload, token);
            if (!response.IsSuccessStatusCode) return await RecordHttpFailureAsync("submit", response, token);
            using var submitted = await response.Content.ReadFromJsonAsync<JsonDocument>(token);
            if (submitted is null || !submitted.RootElement.TryGetProperty("id", out var identifier)
                || identifier.ValueKind != JsonValueKind.String || identifier.GetString() is not { Length: > 0 } id)
                return RecordFailure("submit returned no job id");
            if (warmup) OwnedProcessRegistry.Log("resident_warmup_job", "ImageGeneration", Process.Id, id);
            var poll = Address("jobs/" + Uri.EscapeDataString(id));
            (int Pass, int Step) lastFrame = default;
            while (!Process.HasExited && !Rejected)
            {
                using var result = await Http.GetAsync(poll, token);
                if (!result.IsSuccessStatusCode) return await RecordHttpFailureAsync("poll", result, token);
                using var job = await result.Content.ReadFromJsonAsync<JsonDocument>(token);
                if (job is null) return RecordFailure("poll returned an empty job");
                var state = job.RootElement;
                var status = state.GetProperty("status").GetString();
                switch (status)
                {
                    case "completed":
                        var image = state.GetProperty("result").GetProperty("images")[0].GetProperty("b64_json");
                        if (output is null) return image.ValueKind == JsonValueKind.String && image.GetString() is { Length: > 0 }
                            || RecordFailure("job completed without an image");
                        await File.WriteAllBytesAsync(output, image.GetBytesFromBase64(), token);
                        return true;
                    case "generating":
                        if (preview is not null && state.TryGetProperty("preview", out var frame) && frame.ValueKind == JsonValueKind.Object)
                        {
                            var position = (frame.GetProperty("pass").GetInt32(), frame.GetProperty("step").GetInt32());
                            if (position != lastFrame)
                            {
                                using var stream = new MemoryStream(frame.GetProperty("b64_json").GetBytesFromBase64());
                                var bitmap = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                                bitmap.Freeze();
                                preview.Report(bitmap);
                                lastFrame = position;
                            }
                        }
                        break;
                    case "queued": break;
                    default: return RecordFailure($"job {id} reported {status} {ErrorDetail(state)}");
                }
                await Task.Delay(300, token);
            }
            return RecordFailure(Process.HasExited ? "server exited while generating" : "placement rejected by GPU policy");
        }

        private async Task<bool> RecordHttpFailureAsync(string step, HttpResponseMessage response, CancellationToken token)
        {
            var detail = "";
            try
            {
                using var body = await response.Content.ReadFromJsonAsync<JsonDocument>(token);
                if (body is not null) detail = ErrorDetail(body.RootElement);
            }
            catch (JsonException) { }
            return RecordFailure($"{step} HTTP {(int)response.StatusCode} {detail}");
        }

        private static string ErrorDetail(JsonElement body) => body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("error", out var error) || error.ValueKind == JsonValueKind.Null ? "" : error.ToString();

        private bool RecordFailure(string detail)
        {
            lock (_log) _log.AppendLine("sd-server: " + detail[..Math.Min(detail.Length, 1024)]);
            return false;
        }

        private Uri Address(string path) => new(_endpoint, Api + path);

        private async Task DrainAsync(StreamReader stream)
        {
            while (await stream.ReadLineAsync() is { } line)
            {
                var placement = ImageGenerationPlacementLog.Read(line);
                lock (_log)
                {
                    if (placement != ImageGenerationGpuPlacement.Unknown) _placement = placement;
                    _log.AppendLine(line);
                    if (_log.Length > 128 * 1024) _log.Remove(0, _log.Length - 64 * 1024);
                }
            }
        }

        internal (string Log, ImageGenerationGpuPlacement Placement) ReadLog()
        {
            lock (_log)
            {
                var result = (_log.ToString(), _placement);
                _log.Clear();
                return result;
            }
        }

        internal async Task StopAsync()
        {
            await ModelProcessRetirement.StopAsync(Process, () => ImageGenerationNativeWorker.Stop(Process));
            try { await _logs; }
            finally { Process.Dispose(); }
        }
    }
}
