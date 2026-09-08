using System.IO;
using AIHub.Models;

namespace AIHub.Services;

public static class ImageBatchProfiles
{
    public static bool IsEnabled(string? bundleId) => bundleId is ImageAnalysisBundleCatalog.LightId
        or ImageAnalysisBundleCatalog.MediumId or ImageAnalysisBundleCatalog.HeavyId;

    public static ImageBatchJob Create(string bundleId, string language)
    {
        if (!IsEnabled(bundleId)) throw new InvalidDataException("Batch analysis is unavailable for this mode.");
        var profile = OmniLlamaProfile.ForBundle(bundleId)!;
        return new() { BundleId = bundleId, ModelRevision = profile.Revision, Settings = new() { LanguageCode = language } };
    }

    public static bool CanContinue(ImageBatchJob job) => IsEnabled(job.BundleId)
        && OmniLlamaProfile.ForBundle(job.BundleId)?.Revision == job.ModelRevision;

    public static ImageAnalysisLiterarySession Session(ImageBatchJob job)
    {
        var profile = OmniLlamaProfile.ForBundle(job.BundleId)
            ?? throw new InvalidDataException("Unknown batch model profile.");
        var session = new ImageAnalysisLiterarySession
        {
            SubscenarioId = "literary_batch", File = job.Items.FirstOrDefault()?.File, Settings = job.Settings
        };
        profile.ApplyToNewSession(session);
        session.ModelRevision = job.ModelRevision;
        return session;
    }
}
