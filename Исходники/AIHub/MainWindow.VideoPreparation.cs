using AIHub.Controls.VideoEditor;
using AIHub.Services.VideoEditor;

namespace AIHub;

public partial class MainWindow
{
    private async Task<VideoPreparedModels?> PrepareVideoModelsAsync(VideoEditorWindow owner, VideoModelNeed need, CancellationToken token)
    {
        var root = need.ModelsRoot ?? _storageSettings.Models.Locations.Select(l => l.Path?.Trim()).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)) ?? "";
        using var preparation = new VideoModelPreparation { ParallelConnections = _appSettings.ModelDownloads?.MaximumParallelConnections ?? 0 };
        async Task<bool> Run(bool download, IProgress<AIHub.Models.ManagedModelDownloadProgress> progress, CancellationToken ct) => await preparation.PrepareAsync(root, need, download, progress, ct);
        using var audit = AIHub.Services.ComponentAudit.CaptureOperation();
        if (await Run(false, new Progress<AIHub.Models.ManagedModelDownloadProgress>(), token)) return new(root);
        var name = L("VideoEditor." + (need.Action == "music" ? "MusicModel" : need.Action == "analyze" ? "AnalysisModel" : "VoiceModel"));
        return new VideoPreparationWindow(owner, L, name, Run, token).ShowDialog() == true ? new(root) : null;
    }
}
