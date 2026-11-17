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

/// <summary>Owns the pinned sd.cpp server while the visual workspace is repeating a saved prompt.</summary>
internal sealed class ImageGenerationResident
{
    internal const string Interrupted = "Generation.Qwen.ResidentInterrupted";

    // The older visual worker persisted the transport exception type rather than a recovery reason.
    internal static bool RequiresStandalone(string? detail) => detail is Interrupted or nameof(HttpRequestException);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Session? _session;

    internal ImageGenerationResident() =>
        ApplicationBackgroundOperations.RegisterModel(this, owner => owner.RetireAsync());

    internal async Task RetireAsync()
    {
        await _gate.WaitAsync();
        try { await CloseSessionAsync(); }
        finally { _gate.Release(); }
    }

    internal async Task<(int ExitCode, string Log, ImageGenerationGpuPlacement Placement)> GenerateAsync(
        ImageGenerationRequest request, int index, IReadOnlyList<ManagedModelArtifactCard> cards, string output,
        SdRuntimeSelection selection, IProgress<BitmapSource>? preview, CancellationToken token)
    {
        var command = ImageGenerationNativeWorker.ManagedCommand(request, index, cards, "", output, selection, server: true);
        await _gate.WaitAsync(token);
        try
        {
            if (_session is not { } session || !session.Matches(command))
            {
                await CloseSessionAsync();
                token.ThrowIfCancellationRequested();
                _session = session = new(command);
                await session.WaitUntilReadyAsync(token);
            }
            var success = !session.Rejected && await session.DrawAsync(request, index, output, preview, token);
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
        finally { _gate.Release(); }
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
        private readonly Uri _endpoint;
        private readonly Task _logs;
        private readonly StringBuilder _log = new();
        private ImageGenerationGpuPlacement _placement;
        internal Process Process { get; }

        internal Session(ProcessStartInfo command)
        {
            _executable = command.FileName;
            _arguments = command.ArgumentList.ToArray();
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
                lock (_log) return _placement is ImageGenerationGpuPlacement.GpuUnavailable
                    or ImageGenerationGpuPlacement.MemoryUnavailable;
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
            string output, IProgress<BitmapSource>? preview, CancellationToken token)
        {
            if (Process.HasExited) return false;
            var references = new List<string>(request.ReferenceImages.Length);
            foreach (var path in request.ReferenceImages)
                references.Add(Convert.ToBase64String(await File.ReadAllBytesAsync(path, token)));
            using var response = await Http.PostAsJsonAsync(Address("img_gen"), new
            {
                prompt = request.Prompt, width = request.Width, height = request.Height, seed = request.Seeds[index],
                output_format = "png", embed_image_metadata = false,
                ref_images = references, ref_image_args = ImageGenerationReferences.NativeOptions(request),
                preview = preview is null ? "none" : "proj", preview_interval = 1
            }, token);
            if (!response.IsSuccessStatusCode) return false;
            using var submitted = await response.Content.ReadFromJsonAsync<JsonDocument>(token);
            if (submitted?.RootElement.GetProperty("id").GetString() is not { Length: > 0 } id) return false;
            var poll = Address("jobs/" + Uri.EscapeDataString(id));
            (int Pass, int Step) lastFrame = default;
            while (!Process.HasExited && !Rejected)
            {
                using var job = await Http.GetFromJsonAsync<JsonDocument>(poll, token);
                if (job is null) return false;
                var state = job.RootElement;
                switch (state.GetProperty("status").GetString())
                {
                    case "completed":
                        var image = state.GetProperty("result").GetProperty("images")[0].GetProperty("b64_json");
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
                    default: return false;
                }
                await Task.Delay(300, token);
            }
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
