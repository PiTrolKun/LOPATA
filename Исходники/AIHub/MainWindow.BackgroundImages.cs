using System.IO;
using System.Text.Json;
using System.Windows;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;

namespace AIHub;

public partial class MainWindow
{
    private StorageSettings? _imageSavedStorage;
    private string? _imageSavedSessionId;
    private StorageSettings ActiveImageStorage => _imageSavedSessionId == _imageAnalysisLiterarySession?.SessionId
        ? _imageSavedStorage ?? _storageSettings : _storageSettings;
    private const string ImageBatchBackgroundKind = "image.batch";
    private const string ImageSpeechBackgroundKind = "image.speech";
    private const string ImagePreparationBackgroundKind = "image.prepare";
    private sealed record ImageBatchBackgroundInput(string Id, StorageSettings Storage, ImageBackgroundSpeechOptions? Speech = null);
    private sealed record ImageSpeechBackgroundInput(ImageAnalysisLiterarySession Session, StorageSettings Storage, ImageBackgroundSpeechOptions Options);
    private sealed record ImagePreparationBackgroundInput(string BundleId, string? SessionId, bool SpeechOnly, bool ForceMemory,
        StorageSettings? Storage = null, ImageAnalysisLiterarySession? Session = null, ImageBackgroundSpeechOptions? Speech = null);

    private void RegisterImageBackgroundOperations()
    {
        _backgroundOperations!.Register(ImageBackgroundWork.Kind, ResumeBackgroundImageAsync);
        _backgroundOperations.Register(ImageBatchBackgroundKind, ResumeBackgroundImageBatchAsync);
        _backgroundOperations.Register(ImageSpeechBackgroundKind, async (state, token) =>
        {
            var input = state.Input.Deserialize<ImageSpeechBackgroundInput>()!;
            _imageAnalysisLiterarySession = input.Session;
            _imageSavedStorage = input.Storage; _imageSavedSessionId = input.Session.SessionId;
            _selectedImageAnalysisBundle = ImageAnalysisBundleCatalog.Create().Single(b => b.Id == input.Session.BundleId);
            ShowBackgroundScenarioPage(ImageAnalysisWorkspacePage);
            await SpeakCurrentImageAnalysisSummaryAsync(false, operationToken: token, restored: state, options: input.Options);
        });
        _backgroundOperations.Register(ImagePreparationBackgroundKind, async (state, token) =>
        {
            var input = state.Input.Deserialize<ImagePreparationBackgroundInput>()!;
            _selectedImageAnalysisBundle = ImageAnalysisBundleCatalog.Create().Single(b => b.Id == input.BundleId);
            if (input.Session is not null)
            {
                _imageAnalysisLiterarySession = input.Session;
                _imageSavedStorage = input.Storage; _imageSavedSessionId = input.Session.SessionId;
            }
            else if (input.SessionId is not null) RestoreBackgroundImage(input.SessionId, input.Storage);
            using var owner = CancellationTokenSource.CreateLinkedTokenSource(token);
            _imageAnalysisRuntimePreparationCts = owner;
            if (input.SpeechOnly) await WarmImageAnalysisSpeechAsync(input.ForceMemory, token, state);
            else await PrepareImageAnalysisRuntimeAsync(owner, restored: state);
        });
    }

    private async Task ResumeBackgroundImageAsync(BackgroundOperationState state, CancellationToken token)
    {
        var input = state.Input.Deserialize<ImageBackgroundInput>() ?? throw new InvalidDataException("Missing image input.");
        var session = _imageAnalysisSessionStore.Load(input.SessionId, input.Storage)
            ?? throw new FileNotFoundException("Saved image session is missing.");
        _batchJob = null; _batchStorage = null;
        _imageAnalysisLiterarySession = session;
        RestoreBackgroundImage(input.SessionId, input.Storage);
        if (input.Action == "revise")
            await ReviseBackgroundImageAsync(new(input.Request), state, token);
        else await GenerateBackgroundImageAsync(new(session.Settings), state, token);
    }

    private void RestoreBackgroundImage(string sessionId, StorageSettings? storage = null)
    {
        var session = _imageAnalysisSessionStore.Load(sessionId, storage ?? _storageSettings)
            ?? throw new FileNotFoundException("Saved image result is missing.");
        _imageAnalysisLiterarySession = session;
        _imageSavedStorage = storage ?? _storageSettings; _imageSavedSessionId = session.SessionId;
        _selectedImageAnalysisBundle = ImageAnalysisBundleCatalog.Create().Single(b => b.Id == session.BundleId);
        _imageAnalysisBundleInstallationService.Check(_storageSettings, session.BundleId);
        _imageAnalysisWorkspaceReadOnly = false;
        ImageAnalysisWorkspacePage.ShowSession(session);
        ShowBackgroundScenarioPage(ImageAnalysisWorkspacePage);
    }

    private async Task ResumeBackgroundImageBatchAsync(BackgroundOperationState state, CancellationToken token)
    {
        var input = state.Input.Deserialize<ImageBatchBackgroundInput>() ?? throw new InvalidDataException("Missing batch input.");
        _batchStorage = input.Storage;
        _batchJob = new ImageBatchStore(Path.Combine(Path.GetDirectoryName(_imageAnalysisSessionStore.GetProjectsDirectory(input.Storage))!, "Batches"))
            .LoadAll().Single(j => j.Id == input.Id);
        _batchView = new ImageBatchControl(L);
        _batchView.Action += BatchAction;
        ShowBackgroundScenarioPage(ImageAnalysisWorkspacePage);
        await RunImageBatchAsync(state, token);
    }

    private bool ViewBackgroundImageResult(BackgroundOperationNotice notice)
    {
        var saved = _backgroundOperations!.LoadResult(notice.Id);
        if (saved is null) return false;
        if (notice.Kind == ImagePreparationBackgroundKind)
        { ShowBackgroundResultPreview(notice.Title, L("Status.ImageAnalysisModelsReady")); return true; }
        if (notice.Kind == ImageBatchBackgroundKind)
        {
            var input = saved.Input.Deserialize<ImageBatchBackgroundInput>()!;
            var store = BatchStoreFor(input.Storage);
            var job = store.LoadAll().SingleOrDefault(j => j.Id == input.Id);
            if (job is null || job.Status != "completed" || !ImageBatchExporter.IsPresent(store.Results(job), job)) return false;
            var text = string.Join(Environment.NewLine + Environment.NewLine, job.Items.Where(i => i.Status == "ready")
                .Select(i => store.Read<ImageBatchSection>(job, i.Id, "final"))
                .Where(section => section is not null)
                .Select(section => section!.Title + Environment.NewLine + string.Join(Environment.NewLine, section.Paragraphs)));
            ShowBackgroundResultPreview(notice.Title, text + Environment.NewLine + store.Results(job));
            return true;
        }
        if (notice.Kind == ImageSpeechBackgroundKind)
        {
            var input = saved.Input.Deserialize<ImageSpeechBackgroundInput>()!;
            ShowBackgroundResultPreview(notice.Title, string.Join(Environment.NewLine,
                ImageAnalysisSpeechTextService.BuildSegments(input.Session.ReviewSummary).Select(segment => segment.Text)));
            return true;
        }
        var image = saved.Input.Deserialize<ImageBackgroundInput>()!;
        var session = _imageAnalysisSessionStore.Load(image.SessionId, image.Storage);
        var version = session?.Versions.FirstOrDefault(v => v.VersionId == image.ResultVersionId);
        if (version is null) return false;
        ShowBackgroundResultPreview(notice.Title, version.Text);
        return true;
    }

    private void ShowBackgroundScenarioPage(FrameworkElement selected)
    {
        // Restoring a hidden operation must not cancel work merely by changing visibility.
        ApplicationBackgroundOperations.PreserveHiddenWork = true;
        try
        {
            foreach (var page in new FrameworkElement[] { WelcomePage, SettingsPage, SetupPage, ProfilePage,
                ProfileReminderPage, WorkStartPage, ChoiceScenarioPage, ImageAnalysisWorkspacePage,
                ImageAnalysisBundleConfirmationPage, ImageAnalysisBundleSelectorPage, LiteraryPage })
                page.Visibility = ReferenceEquals(page, selected) ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { ApplicationBackgroundOperations.PreserveHiddenWork = false; }
    }
}
