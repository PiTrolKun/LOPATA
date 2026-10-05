using System.IO;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Pinned music weights shared by preparation, tokenizer and inference.</summary>
public static class MusicComponentCatalog
{
    public const string Repository = "Serveurperso/YuE2-GGUF";
    public const string Revision = "64b030e3deb6e8150d2b7c0db641ef5a17eca8a3";
    public const string ModelId = "music-yue2-q8";
    public const string DecoderId = "music-yue2-vae";
    public static readonly IReadOnlyList<string> ComponentIds = Array.AsReadOnly(new[] { ModelId, DecoderId });

    public static IReadOnlyList<ManagedModelArtifactCard> CreateCards(string root) =>
    [
        Card(root, ModelId, "YuE2-3B Q8_0", "YuE2-3B-Q8_0.gguf", 3_810_232_064,
            "41121ce97786d7795a325bcf123ca196956c03bb252c9e75384cfc1f2fc19e6b", "model_weights"),
        Card(root, DecoderId, "YuE2-Vae F32", "YuE2-Vae-F32.gguf", 530_497_344,
            "93e49dfb1970e89ad64cacb17cf13b5d05f6bb30ef7ed3adae3050bcb728638a", "audio_decoder")
    ];

    public static bool IsComplete(IReadOnlyList<ManagedModelArtifactCard> cards) =>
        cards.Count == ComponentIds.Count && ComponentIds.All(id =>
            cards.Count(c => c.ModelArtifactId == id && c.Status == ManagedModelStatuses.Installed) == 1);

    private static ManagedModelArtifactCard Card(string root, string id, string name, string file,
        long bytes, string hash, string purpose) => new()
    {
        ModelArtifactId = id, DisplayName = name, Family = "YuE2", Role = ManagedModelRoles.Tool,
        Provider = "Hugging Face", RepositoryId = Repository, Revision = Revision, Format = "GGUF",
        License = "CC BY-NC 4.0 + individual creator permission (2026-09-16)",
        SourcePage = "https://huggingface.co/" + Repository, RuntimeBackend = "yue2.cpp",
        IsManaged = true, CanRemoveFiles = true, ModelsRoot = root,
        InstallDirectory = string.IsNullOrWhiteSpace(root) ? "" : Path.Combine(root, "Music", id, Revision[..12]),
        Origin = ManagedModelOrigins.PredefinedScenario,
        Consumers = [new() { Id = ScenarioNavigationCatalog.Music, DisplayName = "Музыка / Music", Kind = "scenario" }],
        Files = [new() { RelativePath = file, SourceUrl = $"https://huggingface.co/{Repository}/resolve/{Revision}/{file}",
            SizeBytes = bytes, Sha256 = hash, Purpose = purpose }]
    };
}
