using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Independent 3B book index; never changes the literary 480M model.</summary>
public static class RagBookModels
{
    public const string EmbeddingId = "model.giga-embeddings-3b";
    public const string AssistantId = "model.gigachat31-10b-q4km";
    public static readonly string[] LicenseIds = [EmbeddingId, AssistantId, ManagedModelCatalog.CoreArtifactId];

    public static IReadOnlyList<ManagedModelArtifactCard> Cards(string modelsRoot)
    {
        if (string.IsNullOrWhiteSpace(modelsRoot)) throw new IOException("RagBooks.StorageRequired");
        var cards = JsonSerializer.Deserialize<List<ManagedModelArtifactCard>>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Tools", "rag-book-models.json")))
            ?? throw new InvalidDataException("Book model manifest is empty.");
        if (cards.Count != 2 || !cards.Select(c => c.ModelArtifactId).ToHashSet().SetEquals([EmbeddingId, AssistantId]))
            throw new InvalidDataException("Unexpected book model manifest.");
        foreach (var card in cards)
        {
            card.ModelsRoot = Path.GetFullPath(modelsRoot);
            card.InstallDirectory = Path.Combine(card.ModelsRoot, "BookPreparation", card.ModelArtifactId, card.Revision);
            card.Family = card.RepositoryId.Split('/').Last();
            card.SourcePage = $"https://huggingface.co/{card.RepositoryId}/tree/{card.Revision}";
            card.IsManaged = true; card.CanRemoveFiles = true;
            card.Origin = ManagedModelOrigins.PredefinedScenario;
            card.Consumers = [new() { Id = ScenarioNavigationCatalog.RagBooks, DisplayName = "RAG book preparation", Kind = "scenario_bundle" }];
            if (card.Files.Count == 0 || card.Revision.Length != 40 || card.Files.Any(f =>
                f.SizeBytes <= 0 || f.Sha256.Length != 64 || !f.Sha256.All(Uri.IsHexDigit)
                || !Uri.TryCreate(f.SourceUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https"
                || uri.Host != "huggingface.co" || !uri.AbsolutePath.StartsWith($"/{card.RepositoryId}/resolve/{card.Revision}/", StringComparison.Ordinal)))
                throw new InvalidDataException("Incomplete pinned book model manifest.");
        }
        return cards;
    }
}

/// <summary>Acquisition only. Verified weights do not establish inference readiness.</summary>
public interface IRagBookModelPreparation : IDisposable
{
    IReadOnlyList<ManagedModelArtifactCard> Cards { get; }
    Task<ManagedModelArtifactCard> PrepareAsync(string id, bool download,
        IProgress<ManagedModelDownloadProgress> progress, CancellationToken token);
}

public sealed class RagBookModelPreparation : IRagBookModelPreparation
{
    private readonly ManagedModelLibraryStore _store;
    private readonly ManagedModelAcquisitionService _downloads;
    public IReadOnlyList<ManagedModelArtifactCard> Cards { get; }

    public RagBookModelPreparation(string modelsRoot, int connections, ManagedModelLibraryStore? store = null)
    {
        _store = store ?? new();
        _downloads = new(_store) { MaximumParallelConnections = connections };
        var core = _store.Load(ManagedModelCatalog.CoreArtifactId) ?? ManagedModelCatalog.CreateCore(modelsRoot);
        Cards = [.. RagBookModels.Cards(modelsRoot), core];
    }

    // A size check is deliberately not called verification; full hashes are checked on request.
    public static bool FilesPresent(ManagedModelArtifactCard card) => card.Files.Count > 0
        && card.Files.All(f => File.Exists(Path.Combine(card.InstallDirectory, f.RelativePath))
            && new FileInfo(Path.Combine(card.InstallDirectory, f.RelativePath)).Length == f.SizeBytes);

    public async Task<ManagedModelArtifactCard> PrepareAsync(string id, bool download,
        IProgress<ManagedModelDownloadProgress> progress, CancellationToken token)
    {
        var definition = Cards.Single(c => c.ModelArtifactId == id);
        if (download) await ComponentLicenseGate.EnsureAsync(id, token);
        var card = _store.Upsert(definition);
        card = await _downloads.VerifyAsync(card.ModelArtifactId, progress, token);
        if (download && card.Status != ManagedModelStatuses.Installed)
            card = await _downloads.DownloadAsync(card.ModelArtifactId, progress, token);
        return card;
    }

    public void Dispose() => _downloads.Dispose();
}
