using System.IO;
using System.Text.Json;

namespace AIHub.Services;

public static class RagLexiconWorker
{
    public static async Task RunAsync(string input, string work, string library, int cpuTarget,
        IProgress<RagBookProgress> progress, CancellationToken token)
    {
        Directory.CreateDirectory(work);
        var arguments = new[] { Path.Combine(AppContext.BaseDirectory, "Tools", "rag_lexicon.py"),
            "--input", input, "--work", work, "--library", library, "--cpu-target", Math.Clamp(cpuTarget, 10, 90).ToString() };
        await GigaEmbeddingInstallation.RunAsync(arguments, line =>
        {
            if (line.StartsWith("[Prepare]", StringComparison.Ordinal)) { progress.Report(new("Verifying", Detail: line)); return; }
            try
            {
                using var doc = JsonDocument.Parse(line); var value = doc.RootElement;
                if (value.TryGetProperty("stage", out var stage)) progress.Report(new(stage.GetString()!,
                    value.TryGetProperty("done", out var done) ? done.GetInt32() : 0,
                    value.TryGetProperty("total", out var total) ? total.GetInt32() : 0,
                    value.TryGetProperty("detail", out var detail) ? detail.GetString() ?? "" : ""));
            }
            catch (JsonException) { /* Runtime output remains in the owned process log. */ }
        }, token, Path.Combine(work, "logs"));
    }
}
