using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.VisualScenario;

internal sealed class VisualSenseNovaSession : IImageGenerationWorker
{
    private const string Component = "Visual.SenseNova";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private Task<string>? _errors;
    private DirectoryInfo? _scratch;
    private string? _root;
    private int _capacity;

    internal VisualSenseNovaSession() =>
        ApplicationBackgroundOperations.RegisterModel(this, session => session.RetireAsync());

    internal bool IsRunning => _process is { HasExited: false };
    internal int? ProcessId => _process is { HasExited: false } process ? process.Id : null;

    internal async Task RetireAsync()
    {
        await _gate.WaitAsync();
        try { await StopAsync(); }
        finally { _gate.Release(); }
    }

    internal Task<int> WarmAsync(string root, CancellationToken token) =>
        WithWorkerAsync(root, _ => Task.FromResult(_capacity), token);

    internal Task<int> CountAsync(string root, IReadOnlyList<object> messages, CancellationToken token) =>
        WithWorkerAsync(root, async worker => (await RequestAsync(worker, new { operation = "count", messages }, token)).GetInt32(), token);

    internal Task<string> CompleteAsync(string root, IReadOnlyList<object> messages, int tokens, CancellationToken token) =>
        WithWorkerAsync(root, async worker => (await RequestAsync(worker,
            new { operation = "chat", messages, max_tokens = tokens }, token)).GetString()
            ?? throw new InvalidDataException("Visual.AdvisorIncomplete"), token);

    public async Task GenerateAsync(ImageGenerationRequest request, int index, IReadOnlyList<ManagedModelArtifactCard> cards,
        string promptFile, string output, CancellationToken token) =>
        _ = await GenerateWithReceiptAsync(request, index, cards, promptFile, output, token);

    public Task<ImageGenerationRuntimeReceipt?> GenerateWithReceiptAsync(ImageGenerationRequest request, int index,
        IReadOnlyList<ManagedModelArtifactCard> cards, string promptFile, string output, CancellationToken token) =>
        WithWorkerAsync<ImageGenerationRuntimeReceipt?>(cards.Single(card => card.ModelArtifactId == VisualSenseNova.Id).InstallDirectory,
            async worker =>
            {
                await RequestAsync(worker, new
                {
                    operation = "draw", prompt = request.Prompt, width = request.Width, height = request.Height,
                    seed = request.Seeds[index], images = request.ReferenceImages, output
                }, token);
                return VisualSenseNova.RenderReceipt();
            }, token);

    private async Task<T> WithWorkerAsync<T>(string root, Func<Process, Task<T>> action, CancellationToken token)
    {
        await ApplicationBackgroundOperations.RetireModelsExceptAsync(this);
        await _gate.WaitAsync(token);
        try
        {
            if (!IsRunning || _root is null || !ManagedModelPathIdentity.SameDirectory(root, _root))
            {
                await StopAsync();
                await StartAsync(root, token);
            }
            return await action(_process!);
        }
        catch
        {
            await StopAsync();
            throw;
        }
        finally { _gate.Release(); }
    }

    private async Task StartAsync(string root, CancellationToken token)
    {
        var runtime = await ManagedPythonRuntime.ResolveAsync("auto", token, new HashSet<string> { VisualSenseNova.Profile },
            requireReliableGpuProbe: true);
        if (runtime.Entry.Id != VisualSenseNova.Profile || runtime.Device == "cpu")
            throw new BackgroundOperationFailedException("Visual.SenseNovaGpuRequired");

        _scratch = Directory.CreateTempSubdirectory("lopata-sensenova-");
        var info = new ProcessStartInfo(runtime.Python)
        {
            UseShellExecute = false, WorkingDirectory = _scratch.FullName, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        info.Environment["HF_HUB_OFFLINE"] = "1";
        info.Environment["TRANSFORMERS_OFFLINE"] = "1";
        foreach (var value in new[] { "-X", "utf8", "-B", Path.Combine(AppContext.BaseDirectory, "Tools", "SenseNova", "worker.py"),
                     "--model-root", root, "--packages", Path.Combine(_scratch.FullName, "packages"), "--device", runtime.Device })
            info.ArgumentList.Add(value);
        var process = _process = OwnedProcessRegistry.Shared.Start(info, Component);
        _errors = ReadErrorsAsync(process.StandardError);
        var ready = await ReadAsync(process, token);
        _capacity = ready.GetProperty("capacity").GetInt32();
        _root = root;
        OwnedProcessRegistry.Log("model_ready", Component, process.Id, $"device={runtime.Device}; capacity={_capacity}");
    }

    private static async Task<JsonElement> RequestAsync(Process process, object request, CancellationToken token)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), token);
        await process.StandardInput.FlushAsync(token);
        var response = await ReadAsync(process, token);
        if (!response.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            throw new BackgroundOperationWaitingException("Visual.SenseNovaFailed");
        return response.GetProperty("value");
    }

    private static async Task<JsonElement> ReadAsync(Process process, CancellationToken token)
    {
        var line = await process.StandardOutput.ReadLineAsync(token);
        if (string.IsNullOrWhiteSpace(line)) throw new BackgroundOperationWaitingException("Visual.SenseNovaFailed");
        using var json = JsonDocument.Parse(line);
        return json.RootElement.Clone();
    }

    private async Task StopAsync()
    {
        var process = _process;
        _root = null;
        if (process is not null)
        {
            var unexpectedlyExited = process.HasExited && process.ExitCode != 0;
            await ModelProcessRetirement.StopAsync(process, () => ImageGenerationNativeWorker.Stop(process));
            _process = null;
            if (_errors is { } errors)
            {
                var diagnostic = await errors;
                if (unexpectedlyExited || diagnostic.Contains("Traceback", StringComparison.Ordinal))
                    OwnedProcessRegistry.Log("request_failed", Component, process.Id, diagnostic);
            }
            _errors = null;
            process.Dispose();
        }
        if (_scratch is { } scratch)
        {
            _scratch = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Lopata.Updates.SafeUpdatePath.RejectLinks(scratch.FullName);
                    scratch.Delete(recursive: true);
                    break;
                }
                catch (DirectoryNotFoundException) { break; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // Windows can hold native extension files briefly after the worker exits.
                    if (attempt < 2) await Task.Delay(TimeSpan.FromSeconds(attempt + 1));
                    else OwnedProcessRegistry.Log("cleanup_failed", Component, detail: error.GetType().Name);
                }
            }
        }
    }

    private static async Task<string> ReadErrorsAsync(StreamReader reader)
    {
        var tail = new StringBuilder();
        var buffer = new char[4096];
        int size;
        while ((size = await reader.ReadAsync(buffer)) > 0)
        {
            tail.Append(buffer, 0, size);
            if (tail.Length > 16384) tail.Remove(0, tail.Length - 16384);
        }
        return tail.ToString();
    }
}
