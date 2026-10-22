using System.IO;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Checks the same installation identity during preparation and immediately before model use.</summary>
internal static class ManagedModelRuntimeAudit
{
    internal static async Task<ManagedModelArtifactCard> VerifyAsync(ManagedModelLibraryStore store, string artifactId,
        CancellationToken token, Action<string>? log = null)
    {
        using var auditLog = ComponentAudit.CaptureLog(log);
        using var downloads = new ManagedModelAcquisitionService(store);
        var card = await downloads.VerifyAsync(artifactId, null, token);
        if (card.Status == ManagedModelStatuses.NeedsVerification
            && artifactId is ManagedModelCatalog.KimiMediumArtifactId or ManagedModelCatalog.FlorenceLargeArtifactId)
            card = await new ImageAnalysisRuntimeCompatibilityService(store).VerifyAsync(artifactId, token);
        if (card.Status != ManagedModelStatuses.Installed)
            throw new InvalidDataException(string.IsNullOrWhiteSpace(card.LastError)
                ? "Prepare the complete model through scenario setup." : card.LastError);
        return card;
    }
}
