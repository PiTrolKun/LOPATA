using AIHub.Models;

namespace AIHub.Services;

public static class OmniSessionCompatibility
{
    public static bool RequiresNewSession(ImageAnalysisLiterarySession session, IOmniTextRuntime runtime)
    {
        if (runtime.BundleId != ImageAnalysisBundleCatalog.LightId) return false;
        if (string.IsNullOrWhiteSpace(session.ModelId))
            return session.HiddenConversation.Count > 0 || session.Versions.Count > 0
                || !string.IsNullOrWhiteSpace(session.VisualReport);
        return !string.Equals(session.ModelId, runtime.ModelId, StringComparison.Ordinal)
            || !string.Equals(session.ModelRevision, runtime.ModelRevision, StringComparison.Ordinal);
    }
}

public sealed class ImageAnalysisModelChangedException()
    : InvalidOperationException("This work belongs to another Alpha model. Start a new analysis to use the current model.")
{
}
