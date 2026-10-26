namespace AIHub.Services.VideoEditor;

internal static class VideoErrors
{
    internal static bool IsKnownInput(Exception error) => error.Message.StartsWith("VideoEditor.", StringComparison.Ordinal)
        && !error.Message.StartsWith("VideoEditor.Runtime", StringComparison.Ordinal);
    internal static void Report(Exception error)
    {
        // Known input/source/selection problems are actionable without auditing components.
        if (IsKnownInput(error)) return;
        ComponentAuditFailures.Report(error);
    }
}
