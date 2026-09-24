using System.IO;
using System.Text.Json;

namespace AIHub.Services.LiteraryImport;

public interface IImportRagBackend : IAsyncDisposable
{
    Task<int> BuildAsync(string id, string input, string vectors, IProgress<LiteraryPreparationProgress> progress, CancellationToken ct);
    Task<bool> VerifyAsync(string id, string vectors, int points, CancellationToken ct);
}

/// <summary>Both import areas use the same embedding worker and checked Qdrant import.</summary>
public sealed class ImportRagBackend(LiteraryProjectLayout layout) : IImportRagBackend
{
    private readonly QdrantRuntime _runtime = layout.CreateRuntime();

    public async Task<int> BuildAsync(string id, string input, string vectors, IProgress<LiteraryPreparationProgress> progress, CancellationToken ct)
    {
        await ComponentLicenseGate.EnsureAsync([GigaEmbeddingInstallation.LicenseId,
            GigaEmbeddingInstallation.RuntimeLicenseId, QdrantOptions.LicenseId], ct);
        // The outer stage owns the initial attempt + three retries. Never multiply nested retries.
        await new GigaSourceEmbedding(retry: false).EmbedAsync(input, vectors, progress, ct);
        return await LiteraryRagImport.ReplaceAsync(_runtime, id, vectors, progress, ct);
    }

    public async Task<bool> VerifyAsync(string id, string vectors, int points, CancellationToken ct)
    {
        if (points <= 0 || !File.Exists(vectors)) return false;
        await _runtime.StartAsync(ct);
        if (await _runtime.LiteraryPointCountAsync(id, ct) != points) return false;
        using var reader = File.OpenText(vectors);
        using var first = JsonDocument.Parse(await reader.ReadLineAsync(ct) ?? "null");
        LiterarySourceIndex.ValidatePoint(first.RootElement);
        await _runtime.VerifyLiterarySearchAsync(id, first.RootElement, ct);
        return true;
    }

    public async ValueTask DisposeAsync() => await _runtime.StopAsync();
}
