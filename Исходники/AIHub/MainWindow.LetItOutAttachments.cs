using AIHub.Controls.VideoEditor;
using AIHub.Services;

namespace AIHub;

public partial class MainWindow
{
    private async Task<bool> PrepareLetItOutImagesAsync(CancellationToken token)
    {
        if (_letItOutCorePath is not { } path) return false;
        using var audit = ComponentAudit.CaptureOperation();
        using var gate = _backgroundOperations?.BeginAuxiliary();
        Task<bool> Run(bool download, IProgress<AIHub.Models.ManagedModelDownloadProgress> progress, CancellationToken ct)
            => LetItOutVision.PrepareAsync(path, download, progress, ct);
        if (await Run(false, new Progress<AIHub.Models.ManagedModelDownloadProgress>(), token)) return true;
        return new VideoPreparationWindow(this, L, L("LetItOut.Attachments.Vision"), Run, token).ShowDialog() == true;
    }
}
