using System.IO;
using AIHub.Models;
using AIHub.Services.OfficeReader;

namespace AIHub.Services.VisualScenario;

internal sealed record VisualPair(string Advisor, string Artist, bool Confirmed = false);

internal sealed partial class VisualComponents(StorageSettings settings, int parallel, IModelUsageGuard? usageGuard = null)
{
    internal const string Core = ManagedModelCatalog.CoreArtifactId;
    internal const string Literary = ManagedModelCatalog.OmniGammaArtifactId;
    internal const string Gemma = ManagedModelCatalog.LetItOutArtifactId;
    internal const string Base = "qwen-image-21-base-gguf";
    internal const string Turbo = "qwen-image-21-turbo-int8";
    private const string VisionEncoder = "generation-qwen-image-21-vision-gguf";
    private const string Revision = "f982a07559d4a2f6c8744d840bf6fccab30eea96";
    private const string Repository = "Qwen/Qwen3-VL-8B-Instruct-GGUF";
    private const string Projector = "mmproj-Qwen3VL-8B-Instruct-F16.gguf";

    internal static readonly string[] Advisors = [Core, Literary, Gemma, VisualSenseNova.Id];
    internal static readonly string[] Artists = [Base, Turbo, VisualSenseNova.Id];
    internal string Root { get; } = settings.Models.Locations.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Path))?.Path ?? "";
    private readonly ManagedModelLibraryStore _library = new();

    internal static int ImageLimit(string artist) => artist == Base ? 10 : 4;
    internal static bool CanSeeImages(string id) => id is Gemma or VisualSenseNova.Id;
    internal string AdvisorName(string id) => id == Literary ? LiteraryModelLocation.DisplayName : AdvisorCard(id).DisplayName;
    internal OutputModelUse AdvisorUse(string id) => new(id, AdvisorName(id), AdvisorCard(id).Revision, "visual-scenario/advisor");

    private ManagedModelArtifactCard AdvisorCard(string id)
    {
        if (!Advisors.Contains(id)) throw new InvalidDataException("Visual.InvalidProject");
        if (id == VisualSenseNova.Id) return VisualSenseNova.Card(Root);
        return _library.Load(id) is { IsManaged: true } card ? card
            : ManagedModelCatalog.CreatePredefined(settings).Single(item => item.ModelArtifactId == id);
    }

    private string AdvisorPath(string id)
    {
        var card = AdvisorCard(id);
        return Path.Combine(card.InstallDirectory, card.Files.First(file => file.Purpose is "main_model" or "executor_model").RelativePath);
    }

    internal bool AdvisorPresent(string id)
    {
        if (id == VisualSenseNova.Id) return VisualSenseNova.Present(Root);
        if (id == Gemma && EngineSetSelection.ActiveId != NativeRuntimeReleasePolicy.UpdatedSet) return false;
        if (!File.Exists(id == Literary ? LiteraryModelLocation.DownloadPath : AdvisorPath(id))
            && (id != Literary || !File.Exists(AdvisorPath(id)) && !File.Exists(LiteraryModelLocation.ConfigurationPath)))
            return false;
        if (id == Gemma)
        {
            var card = ManagedModelCatalog.CreateLetItOutVision(AdvisorCard(id).ModelsRoot);
            if (!File.Exists(Path.Combine(card.InstallDirectory, card.Files[0].RelativePath))) return false;
        }
        return LlamaRuntimeSelector.InstalledBundles().Count > 0;
    }

    internal bool ArtistPresent(string id)
    {
        if (id == VisualSenseNova.Id) return VisualSenseNova.Present(Root);
        if (string.IsNullOrWhiteSpace(Root) || !Artists.Contains(id)) return false;
        var model = ImageGenerationCatalog.Get(id);
        if (!ImageGenerationCatalog.SupportsEngineSet(model, EngineSetSelection.ActiveId) || SdRuntimeSelector.AvailableExecutable() is null) return false;
        var cards = ImageGenerationCatalog.CreateCards(Root).Where(card => model.Components.Contains(card.ModelArtifactId));
        return cards.All(card => card.Files.All(file => File.Exists(Path.Combine(card.InstallDirectory, file.RelativePath))))
            && (id != Base || File.Exists(Path.Combine(VisionDirectory, Projector)));
    }

    internal async Task<string?> PrepareAdvisorAsync(string id, bool download,
        IProgress<ManagedModelDownloadProgress>? progress, CancellationToken token)
    {
        using var audit = ComponentAudit.CaptureOperation();
        if (string.IsNullOrWhiteSpace(Root)) throw new InvalidOperationException("Generation.StorageRequired");
        if (id == VisualSenseNova.Id)
            return (await VisualSenseNova.PrepareAsync(Root, _library, parallel, download, progress, token))?.InstallDirectory;
        if (id == Gemma && EngineSetSelection.ActiveId != NativeRuntimeReleasePolicy.UpdatedSet)
            throw new InvalidOperationException("LetItOut.Preparation.EngineRequired");
        string path;
        if (id == Literary)
        {
            await ComponentLicenseGate.EnsureAsync(Literary, token);
            try { path = await LiteraryModelLocation.ResolveAsync(token); }
            catch (Exception error) when (error is FileNotFoundException or InvalidDataException)
            {
                if (!download) return null;
                var destination = LiteraryModelLocation.DownloadPath;
                var report = new Progress<double>(ratio => progress?.Report(new(Literary, LiteraryModelLocation.FileName,
                    (long)(ratio * LiteraryModelLocation.SizeBytes), LiteraryModelLocation.SizeBytes, 0, "downloading")));
                await LiteraryArtifactDownload.GetAsync(new Uri(LiteraryModelLocation.DownloadUrl), destination,
                    LiteraryModelLocation.SizeBytes, LiteraryModelLocation.Sha256, "sha256", report, token);
                path = await LiteraryModelLocation.ResolveAsync(token);
            }
        }
        else
        {
            using var prepare = new ReaderTextPreparation(_library) { ParallelConnections = parallel };
            if (!await prepare.PrepareAsync(AdvisorCard(id), download, progress, token)) return null;
            path = AdvisorPath(id);
            if (id == Gemma && !await LetItOutVision.PrepareAsync(path, download, progress, token)) return null;
        }
        if (LlamaRuntimeSelector.InstalledBundles().Count > 0) return path;
        if (!download) return null;
        if (EngineSetSelection.ActiveId == NativeRuntimeReleasePolicy.UpdatedSet)
        {
            await DownloadEngineSetAsync(progress, token);
        }
        else
        {
            var report = new Progress<ComponentDownloadProgress>(item => progress?.Report(new(item.ComponentId,
                "llama.cpp", item.DownloadedBytes, item.TotalBytes, item.BytesPerSecond, item.Stage)));
            await new ComponentManager().DownloadAndInstallAsync(HardwareRuntimeCatalog.LlamaCpuId, report, token);
        }
        return LlamaRuntimeSelector.InstalledBundles().Count > 0 ? path : null;
    }

    internal async Task<IReadOnlyList<ManagedModelArtifactCard>?> PrepareArtistAsync(string id, bool images, bool download,
        IProgress<ManagedModelDownloadProgress>? progress, CancellationToken token)
    {
        using var audit = ComponentAudit.CaptureOperation();
        if (!Artists.Contains(id)) throw new InvalidDataException("Visual.InvalidProject");
        if (id == VisualSenseNova.Id)
            return await VisualSenseNova.PrepareAsync(Root, _library, parallel, download, progress, token) is { } sense ? [sense] : null;
        if (!ImageGenerationCatalog.SupportsEngineSet(ImageGenerationCatalog.Get(id), EngineSetSelection.ActiveId))
            throw new InvalidOperationException("Generation.Qwen.EngineSetRequired");
        if (SdRuntimeSelector.AvailableExecutable() is null)
        {
            if (!download) return null;
            await DownloadEngineSetAsync(progress, token);
            if (SdRuntimeSelector.AvailableExecutable() is null) return null;
        }
        using var generation = new ImageGenerationInstallation(_library) { MaximumParallelConnections = parallel };
        IReadOnlyList<ManagedModelArtifactCard> cards;
        try { cards = await generation.PrepareAsync(Root, id, download, progress, token); }
        catch (InvalidOperationException error) when (!download && error.Message == "Generation.DownloadRequired") { return null; }
        if (!images || id == Turbo) return cards;
        var vision = new ManagedModelArtifactCard
        {
            ModelArtifactId = VisionEncoder, DisplayName = "Qwen3-VL-8B image projector",
            RepositoryId = Repository, Revision = Revision, License = "Apache-2.0",
            Provider = "Hugging Face", SourcePage = "https://huggingface.co/" + Repository,
            Role = ManagedModelRoles.Tool, Format = "GGUF", IsManaged = true,
            CanRemoveFiles = true, ModelsRoot = Root,
            InstallDirectory = VisionDirectory,
            Origin = ManagedModelOrigins.PredefinedScenario, RuntimeBackend = "stable-diffusion.cpp",
            Files = [new() { RelativePath = Projector, SourceUrl = $"https://huggingface.co/{Repository}/resolve/{Revision}/{Projector}",
                SizeBytes = 1159029824, Sha256 = "ca524100ebf825c9a870db1c580d03879e0da0ab2541697e2458e64891cf9d38", Purpose = "llm_vision" }]
        };
        await ComponentLicenseGate.EnsureAsync(VisionEncoder, token);
        _library.Upsert(vision);
        using var acquisition = new ManagedModelAcquisitionService(_library) { MaximumParallelConnections = parallel };
        vision = await acquisition.VerifyAsync(VisionEncoder, progress, token);
        if (vision.Status != ManagedModelStatuses.Installed && download)
            vision = await acquisition.DownloadAsync(VisionEncoder, progress, token);
        return vision.Status == ManagedModelStatuses.Installed ? [.. cards, vision] : null;
    }

    private string VisionDirectory => Path.Combine(Root, "ImageGeneration", VisionEncoder, Revision[..12]);

    private static async Task DownloadEngineSetAsync(IProgress<ManagedModelDownloadProgress>? progress, CancellationToken token)
    {
        var sets = EngineSetService.Shared;
        var selected = sets.GetCatalog().Single(set => set.Id == NativeRuntimeReleasePolicy.UpdatedSet);
        await ComponentLicenseGate.EnsureAsync(selected.LicenseIds, token);
        using var installation = ComponentAudit.Installation();
        await sets.DownloadAsync(selected.Id, progress, token);
    }
}
