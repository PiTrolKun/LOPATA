using AIHub.Controls;
using AIHub.Services;
using AIHub.Services.VideoPreparation;

namespace AIHub;

public partial class MainWindow
{
    private void PrepareVideoGenerator(VideoPreparationControl control, VideoGeneratorSettings settings)
    {
        if (settings.ModelId is not (VideoH3Catalog.ModelId or VideoH3LiteCatalog.ModelId or VideoVaceCatalog.ModelId))
        { control.ShowGeneratorPreparationResult(false); return; }
        try
        {
            var root = _storageSettings.Models.Locations.Select(l => l.Path?.Trim()).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)) ?? "";
            var connections = _appSettings.ModelDownloads?.MaximumParallelConnections ?? 0;
            if (settings.ModelId == VideoVaceCatalog.ModelId)
                control.ShowVacePreparation(root, connections);
            else control.ShowH3Preparation(root, connections, lite: settings.ModelId == VideoH3LiteCatalog.ModelId);
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
