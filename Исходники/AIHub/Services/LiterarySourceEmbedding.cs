using System.Text.Json;

namespace AIHub.Services;

public interface ILiterarySourceEmbedding
{
    Task EmbedAsync(string inputPath, string outputPath, IProgress<LiteraryPreparationProgress> progress, CancellationToken ct);
}

public sealed class GigaSourceEmbedding : ILiterarySourceEmbedding
{
    public Task EmbedAsync(string inputPath, string outputPath, IProgress<LiteraryPreparationProgress> progress, CancellationToken ct) =>
        GigaEmbeddingInstallation.RunAsync([GigaEmbeddingInstallation.Script, "--model", GigaEmbeddingInstallation.ModelDirectory,
            "--input", inputPath, "--output", outputPath], line =>
        {
            if (!line.StartsWith('{')) return;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("stage", out var stage))
            {
                var percent = root.TryGetProperty("done", out var done) ? done.GetDouble() * 100 / root.GetProperty("total").GetDouble() : -1;
                progress.Report(new(stage.GetString()!, percent, root.TryGetProperty("device", out var device) ? device.GetString()! : ""));
            }
        }, ct);
}
