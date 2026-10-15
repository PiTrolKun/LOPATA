using System.IO;
using System.Text.Json;

namespace AIHub.Services;

/// <summary>Uses the common verified Python runtime, supported hardware policy and owned process lifecycle.</summary>
public static class RagBookWorker
{
    public static async Task RunAsync(string model, string input, string work, string library, bool query,
        IProgress<RagBookProgress> progress, CancellationToken token, int cpuTargetPercent = 50)
    {
        var arguments = new[] { Path.Combine(AppContext.BaseDirectory, "Tools", "rag_books.py"), "--model", model,
            "--input", input, "--work", work, "--library", library, "--device", "auto",
            "--cpu-target", Math.Clamp(cpuTargetPercent, 10, 90).ToString(System.Globalization.CultureInfo.InvariantCulture) }.ToList();
        if (query) arguments.Add("--query");
        var actualDevice = "unknown";
        void Read(string line)
        {
            if (line.StartsWith("[Prepare]", StringComparison.Ordinal)) { progress.Report(new("Verifying", Detail: line)); return; }
            try
            {
                using var doc = JsonDocument.Parse(line); var item = doc.RootElement;
                if (item.TryGetProperty("device", out var placement)) actualDevice = placement.GetString() ?? "unknown";
                if (item.TryGetProperty("stage", out var stage))
                    progress.Report(new(stage.GetString()!, item.TryGetProperty("done", out var done) ? done.GetInt32() : 0,
                        item.TryGetProperty("total", out var total) ? total.GetInt32() : 0,
                        item.TryGetProperty("device", out var device) ? device.GetString()! : item.TryGetProperty("detail", out var detail) ? detail.GetString()! : ""));
            }
            catch (JsonException) { /* Native/model logging is retained in the common worker journal. */ }
        }
        async Task ExecuteAsync()
        {
            for (var attempt = 0; ; attempt++)
            {
                try { await GigaEmbeddingInstallation.RunAsync(arguments, Read, token, Path.Combine(work, "logs")); return; }
                catch (LiteraryEmbeddingException error) when (attempt == 0 && !error.Message.Contains("Traceback", StringComparison.Ordinal))
                { progress.Report(new("RestartingWorker")); }
            }
        }
        try { await ExecuteAsync(); }
        catch (LiteraryEmbeddingException error) when (error.Message.Contains("RAG_BOOK_GPU_OOM", StringComparison.Ordinal)
            || GigaDeviceFallbackPolicy.CanRetryOnCpu("auto", actualDevice, error, token.IsCancellationRequested))
        {
            progress.Report(new("CpuFallback")); arguments[arguments.IndexOf("--device") + 1] = "cpu";
            await GigaEmbeddingInstallation.RunAsync(arguments, Read, token, Path.Combine(work, "logs"));
        }
        catch (LiteraryEmbeddingException error) when (error.Message.Contains("No admissible text", StringComparison.Ordinal))
        { throw new InvalidDataException("RagBooks.NoAdmissibleText", error); }
    }

    public static async Task<string> SearchAsync(string library, string modelsRoot, string query,
        IProgress<RagBookProgress> progress, CancellationToken token, string? queryRoot = null)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 2000) throw new InvalidDataException("RagBooks.InvalidQuery");
        var work = Path.Combine(queryRoot ?? Path.Combine(library, "queries"), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        RagBookPreparation.AtomicJson(Path.Combine(work, "query.json"), new { query });
        using var preparation = new RagBookModelPreparation(modelsRoot, 0);
        var card = await preparation.PrepareAsync(RagBookModels.EmbeddingId, false, new InlineProgress<Models.ManagedModelDownloadProgress>(_ => { }), token);
        if (card.Status != Models.ManagedModelStatuses.Installed) throw new InvalidDataException("RagBooks.FilesMissing");
        await ComponentLicenseGate.EnsureAsync(RagBookModels.EmbeddingId, token);
        await RunAsync(card.InstallDirectory, Path.Combine(work, "query.json"), work, library, true, progress, token);
        return await File.ReadAllTextAsync(Path.Combine(work, "hits.json"), token);
    }
}
