using System.Text.Json;

namespace AIHub.Services;

public interface ILiterarySourceEmbedding
{
    Task EmbedAsync(string inputPath, string outputPath, IProgress<LiteraryPreparationProgress> progress, CancellationToken ct);
}

public sealed class LiteraryEmbeddingException(string message) : Exception(message);

public static class LiteraryEmbeddingRetry
{
    public static async Task RunAsync(Func<Task> run, IProgress<LiteraryPreparationProgress> progress, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested(); progress.Report(new("Attempt", -1, $"{attempt}/3"));
            try { await run(); return; }
            catch (LiteraryEmbeddingException) when (attempt < 3) { }
        }
    }
}

public sealed class GigaSourceEmbedding(string device = "auto", bool query = false, bool retry = true) : ILiterarySourceEmbedding
{
    public async Task EmbedAsync(string inputPath, string outputPath, IProgress<LiteraryPreparationProgress> progress, CancellationToken ct)
    {
        var requestedDevice = device;
        var actualDevice = "unknown";
        if (retry) await LiteraryEmbeddingRetry.RunAsync(RunWithCpuFallback, progress, ct);
        else await RunWithCpuFallback();
        async Task RunWithCpuFallback()
        {
            try { await Run(); }
            catch (LiteraryEmbeddingException error) when (GigaDeviceFallbackPolicy.CanRetryOnCpu(
                requestedDevice, actualDevice, error, ct.IsCancellationRequested))
            {
                // RunAsync has already retired the worker. Preserve only validated checkpoint rows.
                requestedDevice = "cpu"; actualDevice = "unknown";
                progress.Report(new("Attempt", -1, "GPU hardware failure; retrying on CPU"));
                await Run();
            }
        }
        Task Run() => GigaEmbeddingInstallation.RunAsync(new[] { GigaEmbeddingInstallation.Script, "--model", GigaEmbeddingInstallation.ModelDirectory,
            "--input", inputPath, "--output", outputPath, "--device", requestedDevice }.Concat(query ? new[] { "--query" } : []), line =>
        {
            if (!line.StartsWith('{')) return;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("device", out var placement)) actualDevice = placement.GetString() ?? "unknown";
            if (root.TryGetProperty("stage", out var stage))
            {
                var percent = root.TryGetProperty("done", out var done) ? done.GetDouble() * 100 / root.GetProperty("total").GetDouble() : -1;
                progress.Report(new(stage.GetString()!, percent, root.TryGetProperty("device", out var device) ? device.GetString()! : ""));
            }
        }, ct, System.IO.Path.Combine(System.IO.Path.GetDirectoryName(outputPath)!, "Logs"));
    }
}
