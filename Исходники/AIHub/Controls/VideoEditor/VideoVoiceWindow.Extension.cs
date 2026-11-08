using AIHub.Services.VideoEditor;

namespace AIHub.Controls.VideoEditor;

/// <summary>Reuse the catalog and parameter widgets with a separate caller-owned profile and trial lifecycle.</summary>
internal sealed record VideoVoiceWindowExtension(string Title, string ToggleLabel, Func<string, string> TestPhrase,
    Func<VideoNarratorChoice, string, CancellationToken, Task<string>> Generate,
    Action OpenFolder, Func<Task> BeforeRemove, IReadOnlyDictionary<string, VideoNarratorChoice> Profiles,
    bool ShowToggle = true, string? DefaultModelId = null,
    Func<string, string>? ModelSize = null, Func<string, bool>? FilesPresent = null);

internal sealed partial class VideoVoiceWindow
{
    private readonly VideoVoiceWindowExtension? _extension;
    internal IReadOnlyDictionary<string, VideoNarratorChoice> Profiles => _drafts;
}
