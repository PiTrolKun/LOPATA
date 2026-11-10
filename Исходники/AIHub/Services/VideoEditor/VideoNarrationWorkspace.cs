namespace AIHub.Services.VideoEditor;

/// <summary>A stable, operation-owned workspace lets a resumed narration reuse verified completed packets.</summary>
internal static class VideoNarrationWorkspace
{
    private static readonly AsyncLocal<string?> Current = new();
    private static readonly AsyncLocal<bool> PrivateOutput = new();
    internal static string? Directory => Current.Value;
    internal static bool PublishOutput => !PrivateOutput.Value;
    internal static IDisposable Capture(string directory, bool publishOutput = true)
    {
        var before = Current.Value; var wasPrivate = PrivateOutput.Value;
        Current.Value = directory; PrivateOutput.Value = !publishOutput; return new Restore(before, wasPrivate);
    }
    private sealed class Restore(string? before, bool wasPrivate) : IDisposable
    { public void Dispose() { Current.Value = before; PrivateOutput.Value = wasPrivate; } }
}
