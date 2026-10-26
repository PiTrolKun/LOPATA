using AIHub.Services.VideoEditor;

namespace AIHub.Controls.VideoEditor;

internal sealed partial class VideoEditorWindow
{
    internal Func<VideoModelNeed, CancellationToken, Task<VideoPreparedModels?>>? PrepareModels { get; set; }
    internal static VideoModelNeed ModelNeed(VideoProject p, string action, string[]? selected, bool whole)
    {
        string[] voices = action != "speech" ? [] : whole ? new[] { p.DefaultVoice }
            : p.Narration.Where(n => !string.IsNullOrWhiteSpace(n.Text) && (selected is null || selected.Contains(n.Id))).Select(n => n.Provider).Distinct().ToArray();
        return new(action, voices);
    }
    internal async Task<bool> PrepareRestoredWorkAsync(VideoWorkRequest request, CancellationToken token)
    {
        if (request.Action is not ("music" or "analyze" or "speech") || PrepareModels is null) return true;
        return await PrepareModels(ModelNeed(Project, request.Action, request.Segments, request.Whole) with { ModelsRoot = request.Music?.ModelsRoot }, token) is not null;
    }
}
