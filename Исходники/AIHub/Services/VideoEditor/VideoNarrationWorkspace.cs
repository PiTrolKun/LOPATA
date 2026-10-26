namespace AIHub.Services.VideoEditor;

/// <summary>A stable, operation-owned workspace lets a resumed narration reuse verified completed packets.</summary>
internal static class VideoNarrationWorkspace
{
    private static readonly AsyncLocal<string?> Current = new();
    internal static string? Directory => Current.Value;
    internal static IDisposable Capture(string directory)
    {
        var before = Current.Value; Current.Value = directory; return new Restore(before);
    }
    private sealed class Restore(string? before) : IDisposable { public void Dispose() => Current.Value = before; }
}
