using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.VisualScenario;

internal static class VisualSenseNova
{
    internal const string Id = "sensenova-u1-5";
    internal const string Name = "SenseNova U1.5";
    internal const string Revision = "a2462cc1b549bace2c0896143bbbcbb3fa217c76";
    internal const string SourceRevision = "414f82e9ff458a33190c47639a798994cc53d2a0";
    internal const string Profile = HardwareRuntimeCatalog.PythonCudaId;
    internal const string Backend = "SenseNova / PyTorch";
    private const string Repository = "sensenova/SenseNova-U1.5-8B-MoT";
    private const string License = "Apache-2.0";
    internal static readonly ImageGenerationModel Model = new(Id, Name, [Id], 8, 1, "euler", 0, 1024, 1024, 2048)
        { RequiresGpu = true };

    private static readonly Lazy<ManagedModelArtifactFile[]> Files = new(() =>
    {
        using var stream = typeof(VisualSenseNova).Assembly.GetManifestResourceStream("AIHub.SenseNovaFiles")!;
        return JsonSerializer.Deserialize<ManagedModelArtifactFile[]>(stream)!;
    });

    internal static ImageGenerationArtifact Artifact => new(Id, Name, Repository, Revision, License, Files.Value);

    internal static ImageGenerationRuntimeReceipt RenderReceipt() => new(null, SourceRevision, SourceRevision, Backend, Profile)
        { ArtistRevision = Revision, Placement = "balanced", Schedule = "8-step V2" };

    internal static ImageGenerationRuntimeReceipt? NormalizeReceipt(ImageGenerationRuntimeReceipt? receipt) =>
        receipt is { EngineSetId: "managed-python", ComponentId: Profile } ? receipt with { EngineSetId = null } : receipt;

    internal static ManagedModelArtifactCard Card(string root) => new()
    {
        ModelArtifactId = Id, DisplayName = Name, RepositoryId = Repository,
        Revision = Revision, License = License, Provider = "SenseNova", Role = ManagedModelRoles.Tool,
        SourcePage = "https://huggingface.co/" + Repository, Format = "Safetensors",
        IsManaged = true, CanRemoveFiles = true, ModelsRoot = root,
        InstallDirectory = Path.Combine(root, "VisualScenario", Id, Revision[..12]),
        Origin = ManagedModelOrigins.PredefinedScenario, RuntimeBackend = Backend,
        Files = [.. Files.Value]
    };

    internal static bool RuntimePresent() => new ComponentManager().GetStatus().Any(status =>
        status.Entry.Id == Profile && status.IsAvailable);

    internal static bool Present(string root) => !string.IsNullOrWhiteSpace(root) && RuntimePresent()
        && HasFiles(Card(root));

    private static bool HasFiles(ManagedModelArtifactCard card) => card.Files.All(file =>
        new FileInfo(Path.Combine(card.InstallDirectory, file.RelativePath)) is { Exists: true } info
        && info.Length == file.SizeBytes);

    internal static async Task<ManagedModelArtifactCard?> PrepareAsync(string root, ManagedModelLibraryStore library,
        int parallel, bool download, IProgress<ManagedModelDownloadProgress>? progress, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException("Generation.StorageRequired");
        var card = Card(root);
        if (!download && (!RuntimePresent() || !HasFiles(card))) return null;
        await ComponentLicenseGate.EnsureAsync(Id, token);
        if (!RuntimePresent())
        {
            var report = new Progress<ComponentDownloadProgress>(p => progress?.Report(new(p.ComponentId,
                "PyTorch", p.DownloadedBytes, p.TotalBytes, p.BytesPerSecond, p.Stage)));
            await new ComponentManager().DownloadAndInstallAsync(Profile, report, token);
            if (!RuntimePresent()) return null;
        }
        var stored = library.Load(Id);
        if (stored is null || !ManagedModelPathIdentity.SameDirectory(stored.InstallDirectory, card.InstallDirectory)
            || !stored.Files.Select(file => (file.RelativePath, file.SizeBytes, file.Sha256))
                .SequenceEqual(card.Files.Select(file => (file.RelativePath, file.SizeBytes, file.Sha256))))
            library.Upsert(card);
        using var acquisition = new ManagedModelAcquisitionService(library) { MaximumParallelConnections = parallel };
        var prepared = await acquisition.VerifyAsync(Id, progress, token);
        if (prepared.Status != ManagedModelStatuses.Installed && download)
            prepared = await acquisition.DownloadAsync(Id, progress, token);
        return prepared.Status == ManagedModelStatuses.Installed ? prepared : null;
    }
}
