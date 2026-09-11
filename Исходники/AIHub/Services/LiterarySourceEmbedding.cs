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

public sealed class GigaSourceEmbedding(string device = "auto", bool query = false) : ILiterarySourceEmbedding
{
    public async Task EmbedAsync(string inputPath, string outputPath, IProgress<LiteraryPreparationProgress> progress, CancellationToken ct)
    {
        await LiteraryEmbeddingRetry.RunAsync(Run, progress, ct);
        Task Run() => GigaEmbeddingInstallation.RunAsync(new[] { GigaEmbeddingInstallation.Script, "--model", GigaEmbeddingInstallation.ModelDirectory,
            "--input", inputPath, "--output", outputPath, "--device", device }.Concat(query ? new[] { "--query" } : []), line =>
        {
            if (!line.StartsWith('{')) return;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("stage", out var stage))
            {
                var percent = root.TryGetProperty("done", out var done) ? done.GetDouble() * 100 / root.GetProperty("total").GetDouble() : -1;
                progress.Report(new(stage.GetString()!, percent, root.TryGetProperty("device", out var device) ? device.GetString()! : ""));
            }
        }, ct, System.IO.Path.Combine(System.IO.Path.GetDirectoryName(outputPath)!, "Logs"));
    }
}
