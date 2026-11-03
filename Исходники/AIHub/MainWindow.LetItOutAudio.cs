using AIHub.Controls.VideoEditor;
using AIHub.Services;

namespace AIHub;

public partial class MainWindow
{
    private async Task<bool> PrepareLetItOutAudioAsync(CancellationToken token)
    {
        using var audit = ComponentAudit.CaptureOperation();
        using var gate = _backgroundOperations?.BeginAuxiliary();
        Task<bool> Run(bool download, IProgress<AIHub.Models.ManagedModelDownloadProgress> progress, CancellationToken ct)
            => LetItOutAudioTranscription.PrepareAsync(_componentManager, download, progress, ct);
        if (await Run(false, new Progress<AIHub.Models.ManagedModelDownloadProgress>(), token)) return true;
        return new VideoPreparationWindow(this, L, L("LetItOut.Attachments.AudioKind"), Run, token).ShowDialog() == true;
    }
}
