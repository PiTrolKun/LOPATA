using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;

namespace AIHub;

public partial class MainWindow
{
    private ImageAssetStore? _imageAssets;
    private HttpClient? _imageInputHttp;
    private CancellationTokenSource? _imageImportCts;

    private void InitializeImageInput()
    {
        _imageAssets = new ImageAssetStore(Path.Combine(AppDataPaths.RuntimeDirectory, "ImageImports"));
        _imageInputHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        Activated += (_, _) => ImageAnalysisWorkspacePage.RefreshImageAvailability();
        PreviewKeyDown += ImageInputWindow_KeyDown;
        InitializeImageBatch();
        ImageAnalysisWorkspacePage.ImageInputRequested += async (_, e) => await ImportWorkspaceImageAsync(e.Input);
    }

    private void ImageInputWindow_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (IsActive && ImageAnalysisWorkspacePage.IsVisible && _batchView?.AcceptsInput == true && !e.IsRepeat
            && e.Key == System.Windows.Input.Key.V && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
        {
            e.Handled = true;
            try { var data = System.Windows.Clipboard.GetDataObject(); if (data is not null) _ = ImportBatchTransferAsync(data); }
            catch { StatusText.Text = L("ImageInput.ClipboardBusy"); }
            return;
        }
        if (!IsActive || !ImageAnalysisWorkspacePage.IsVisible || e.IsRepeat
            || e.Key != System.Windows.Input.Key.V
            || System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.Control
            || !ImageAnalysisWorkspacePage.CanPasteImage(System.Windows.Input.Keyboard.FocusedElement)) return;
        e.Handled = true;
        ImageAnalysisWorkspacePage.PasteImageFromClipboard();
    }

    private void EndImageInputSession()
    {
        CleanupBatchInputs();
        _imageImportCts?.Cancel();
        _imageAssets?.EndSession(_imageAnalysisLiterarySession);
    }

    private async Task ImportWorkspaceImageAsync(ImageInput input)
    {
        var original = _imageAnalysisLiterarySession;
        if (original is null || !ImageAnalysisWorkspacePage.CanImportImage || _imageImportCts is not null
            || _imageAssets is null || _imageInputHttp is null) return;
        using var cancellation = new CancellationTokenSource();
        _imageImportCts = cancellation;
        ImageAnalysisFilePassport? imported = null;
        var committed = false;
        string? failure = null;
        ImageAnalysisWorkspacePage.SetBusy(string.Empty, L("ImageInput.Checking"));
        try
        {
            imported = await new ImageInputService(_imageAssets, _imageInputHttp).ImportAsync(input, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(original, _imageAnalysisLiterarySession)) return;
            if (imported.StorageKind == ImageAssetKinds.Temporary)
            {
                var choice = System.Windows.MessageBox.Show(this,
                    L("ImageInput.RetentionQuestion"), L("ImageInput.RetentionTitle"),
                    MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
                if (choice == MessageBoxResult.Cancel) return;
                cancellation.Token.ThrowIfCancellationRequested();
                if (choice == MessageBoxResult.Yes)
                {
                    imported.SourcePath = _imageAssets.Keep(imported.SourcePath, _storageSettings);
                    imported.StorageKind = ImageAssetKinds.Saved;
                    imported.DisplayName = Path.GetFileName(imported.SourcePath);
                }
            }
            // Persist a separate candidate: cancellation, invalid input and save failure cannot erase old versions.
            var candidate = JsonSerializer.Deserialize<ImageAnalysisLiterarySession>(JsonSerializer.Serialize(original))!;
            candidate.File = imported;
            candidate.VisualReport = string.Empty;
            candidate.HiddenConversation.Clear();
            candidate.AnalysisLanguageCode = string.Empty;
            candidate.Observations.Clear();
            candidate.ReviewSummary = new ImageAnalysisReviewSummary();
            candidate.SpeechResult = new ImageAnalysisSpeechResult();
            candidate.RuntimeMetrics = new ImageAnalysisRuntimeMetrics();
            candidate.Events.Clear();
            candidate.Versions.Clear();
            candidate.SelectedVersionId = string.Empty;
            candidate.CompletedAt = null;
            candidate.InternalImageCopyPath = string.Empty;
            candidate.InternalDescriptionCopyPath = string.Empty;
            candidate.Status = ImageAnalysisLiteraryStatuses.FileReady;
            candidate.CurrentStep = ImageAnalysisLiterarySteps.Image;
            candidate.LastError = string.Empty;
            AddImageAnalysisEvent(candidate, ImageAnalysisEventCodes.FileReady, string.Empty,
                ImageAnalysisEventStatuses.Completed, imported.DisplayName);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(original, _imageAnalysisLiterarySession)) return;
            CancelImageAnalysisSpeech();
            _sessionAudioPlayer?.Clear();
            _imageAnalysisSessionStore.Save(candidate, _storageSettings);
            _imageAnalysisLiterarySession = candidate;
            committed = true;
            if (!string.Equals(original.File?.SourcePath, imported.SourcePath, StringComparison.OrdinalIgnoreCase))
                _imageAssets.EndSession(original);
            ImageAnalysisWorkspacePage.ShowImageStep(candidate);
            ImageAnalysisWorkspacePage.SetValidatedFile(candidate);
            RefreshImageAnalysisSpeechUi();
            StatusText.Text = LF("Status.ImageAnalysisFileSelected", imported.DisplayName);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            failure = L(ex is ImageInputException known ? known.Key : "ImageInput.Failed");
        }
        finally
        {
            if (!committed && imported?.StorageKind == ImageAssetKinds.Temporary)
                _imageAssets.DeleteTemporary(imported.SourcePath);
            if (ReferenceEquals(_imageImportCts, cancellation)) _imageImportCts = null;
            if (committed || ReferenceEquals(original, _imageAnalysisLiterarySession))
                ImageAnalysisWorkspacePage.StopActivity();
            if (!committed && ReferenceEquals(original, _imageAnalysisLiterarySession))
            {
                ImageAnalysisWorkspacePage.ShowSession(original);
                if (failure is not null) ImageAnalysisWorkspacePage.SetOperationError(failure);
            }
        }
    }

    private void OpenImagesFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var directory = ImageAssetStore.ImagesDirectory(_storageSettings);
            Directory.CreateDirectory(Path.Combine(directory, "Saved"));
            Directory.CreateDirectory(Path.Combine(directory, "Generated"));
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception) { System.Windows.MessageBox.Show(this, L("ImageInput.FolderError"), L("ImageInput.Folder")); }
    }
}
