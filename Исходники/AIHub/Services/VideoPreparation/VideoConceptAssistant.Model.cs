using System.IO;
using AIHub.Models;

namespace AIHub.Services.VideoPreparation;

public sealed partial class VideoConceptAssistant
{
    private static async Task<(DebugModelInfo Model, OutputModelUse Identity)> AuditModelAsync(VideoConceptModelRequest request, CancellationToken attempt)
    {
        var candidate = MusicPoetryModelCatalog.All.Single(c => c.Id == request.ModelId);
        await ComponentLicenseGate.EnsureAsync(candidate.LicenseId, attempt);
        var model = new DebugModelInfo
        {
            Name = VideoConceptModels.Name(request.ModelId),
            Path = request.ModelPath,
            Format = "gguf",
            IsRunnable = true,
            IsCoreModel = request.ModelId == "core",
            SizeBytes = new FileInfo(request.ModelPath).Length
        };
        if (MusicPoetryModelCatalog.Find(model)?.Id != request.ModelId || !MusicPoetryModelCatalog.IsCompatible(model))
            throw new InvalidDataException("VideoPrep.AiModelMissing");
        var library = new ManagedModelLibraryStore();
        var card = library.LoadAll().FirstOrDefault(c => c.Files.Any(f =>
            Path.GetFullPath(Path.Combine(c.InstallDirectory, f.RelativePath)).Equals(Path.GetFullPath(request.ModelPath), StringComparison.OrdinalIgnoreCase)));
        if (card is not null)
        {
            using var audit = new ManagedModelAcquisitionService(library);
            card = await audit.VerifyAsync(card.ModelArtifactId, null, attempt);
            if (card.Status is not (ManagedModelStatuses.Installed or ManagedModelStatuses.NeedsVerification))
                throw new InvalidDataException("VideoPrep.AiModelUnverified");
        }
        else if (request.ModelId == "core") await CoreModelManager.VerifyPathForUseAsync(request.ModelPath, attempt);
        else throw new InvalidDataException("VideoPrep.AiModelUnverified");
        var weight = card?.Files.FirstOrDefault(f => Path.GetFullPath(Path.Combine(card.InstallDirectory, f.RelativePath))
            .Equals(Path.GetFullPath(request.ModelPath), StringComparison.OrdinalIgnoreCase));
        var revision = card is null ? "unknown" : card.Revision + (weight?.Sha256 is { Length: 64 } hash ? "; sha256=" + hash : "");
        return (model, new(candidate.LicenseId, VideoConceptModels.Name(request.ModelId), revision, "video preparation"));
    }
}
