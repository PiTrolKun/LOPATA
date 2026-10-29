using AIHub.Controls;
using AIHub.Services;
using AIHub.Services.VideoPreparation;

namespace AIHub;

public partial class MainWindow
{
    private void PrepareVideoGenerator(VideoPreparationControl control, VideoGeneratorSettings settings)
    {
        if (settings.ModelId != VideoH3Catalog.ModelId) { control.ShowGeneratorPreparationResult(false); return; }
        try
        {
            var root = _storageSettings.Models.Locations.Select(l => l.Path?.Trim()).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)) ?? "";
            control.ShowH3Preparation(root, _appSettings.ModelDownloads?.MaximumParallelConnections ?? 0);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            ComponentAuditFailures.Report(error);
            System.Windows.MessageBox.Show(this, error.Message.StartsWith("Video", StringComparison.Ordinal) ? L(error.Message) : error.Message,
                L("VideoEditor.ModelPreparation"));
        }
    }
}
