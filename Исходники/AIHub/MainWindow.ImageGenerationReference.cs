using AIHub.Controls;
using AIHub.Services;

namespace AIHub;

public partial class MainWindow
{
    private ImageReferenceAnalyzer? _imageReferenceAnalyzer;
    private void ConfigureGenerationReferences()
    {
        _imageReferenceAnalyzer ??= new(_imageAnalysisBundleInstallationService.LibraryStore);
        GenerationPage.ConfigureReferences(_imageReferenceAnalyzer,
            () => _imageAnalysisBundleInstallationService.Check(_storageSettings, ImageAnalysisBundleCatalog.MediumId),
            snapshot =>
            {
                var window = new ImageReferencePreparationWindow(snapshot, L, async (action, progress, token) =>
                {
                    _imageAnalysisBundleInstallationService.MaximumParallelConnections = _appSettings.ModelDownloads?.MaximumParallelConnections ?? 0;
                    return action == ImageAnalysisBundleActions.Download
                        ? await _imageAnalysisBundleInstallationService.DownloadMissingAsync(_storageSettings, progress, token, ImageAnalysisBundleCatalog.MediumId)
                        : await _imageAnalysisBundleInstallationService.VerifyAsync(_storageSettings, progress, token, ImageAnalysisBundleCatalog.MediumId);
                }, () => _imageAnalysisBundleInstallationService.Check(_storageSettings, ImageAnalysisBundleCatalog.MediumId)) { Owner = this };
                window.ShowDialog(); return Task.FromResult(window.Snapshot);
            });
    }
}
