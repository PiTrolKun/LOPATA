using System.IO;
using System.Windows;
using AIHub.Controls;
using AIHub.Controls.OfficeReader;
using AIHub.Controls.VectorEditor;
using AIHub.Controls.VideoEditor;
using AIHub.Controls.VideoUpscale;
using AIHub.Services;
using AIHub.Services.OfficeEditor;
using AIHub.Services.VectorEditor;
using AIHub.Services.VideoEditor;
using AIHub.Services.VideoUpscale;

namespace AIHub;

public partial class MainWindow
{
    private const string FileViewerRecoveryKind = "FileViewer";

    private CrashWorkspaceWindow? CaptureCrashWorkspaceWindow(Window window)
    {
        if (CaptureCrashWindowLayout(window) is not { } layout) return null;
        var visible = window.IsVisible;
        return window switch
        {
            UpscaleWindow upscale => new(ScenarioNavigationCatalog.VideoUpscale, upscale.Project.Id, null,
                upscale.RecoveryPreviewPosition, visible, layout),
            VideoEditorWindow video => new(ScenarioNavigationCatalog.VideoEditor, video.ProjectId, video.RecoveryFilePath,
                video.RecoveryPreviewPosition, visible, layout, video.RecoveryHasUnsavedChanges),
            VectorEditorWindow vector => new(ScenarioNavigationCatalog.VectorEditor, vector.ProjectId, vector.RecoveryFilePath,
                null, visible, layout),
            ReaderWindow reader => new(reader.IsEditing ? ScenarioNavigationCatalog.OfficeEditor : ScenarioNavigationCatalog.OfficeReader,
                reader.CrashRecoveryDraftId, reader.SourcePath, null, visible, layout),
            FileViewerWindow viewer when viewer.Owner == this => new(FileViewerRecoveryKind, null,
                viewer.RecoveryFilePath, null, visible, layout),
            LyricVideoWindow lyrics => new(ScenarioNavigationCatalog.LyricVideo, null, lyrics.SourcePath, null, visible, layout),
            _ when window == _bookConversionWindow => new(ScenarioNavigationCatalog.BookConversion, null, null, null, visible, layout),
            _ when window == _metadataWindow => new(ScenarioNavigationCatalog.Metadata, null, null, null, visible, layout),
            _ when window == _publisherWindow => new(ScenarioNavigationCatalog.Publisher, null, null, null, visible, layout),
            _ when window == _dictationWindow => new(ScenarioNavigationCatalog.Dictation, null, null, null, visible, layout),
            _ => null
        };
    }

    private async Task RestoreCrashWorkspaceWindowAsync(CrashWorkspaceWindow entry)
    {
        var file = entry.File is { } path && Path.IsPathFullyQualified(path) && File.Exists(path) ? path : null;
        var projectId = Guid.TryParseExact(entry.Project, "N", out _) ? entry.Project : null;
        var restoreOfficeReading = false;
        Window? window;
        switch (entry.Kind)
        {
            case ScenarioNavigationCatalog.VideoUpscale:
                if (_upscaleWindow is null && projectId is not null && File.Exists(UpscaleStore.ProjectPath(projectId)))
                    OpenUpscaleUtility(UpscaleStore.Load<UpscaleProject>(UpscaleStore.ProjectPath(projectId)));
                window = _upscaleWindow;
                break;
            case ScenarioNavigationCatalog.VideoEditor:
                var videoRecovery = projectId is null ? null : VideoProjectStore.Recovery(projectId);
                if (_videoEditor is null && videoRecovery is not null && File.Exists(videoRecovery))
                {
                    ShowVideoEditor(VideoProjectStore.Load(videoRecovery), file);
                    if (entry.Dirty) _videoEditor!.MarkCrashRecoveryDraft();
                }
                else if (_videoEditor is null) OpenVideoEditorUtility(file);
                window = _videoEditor;
                break;
            case ScenarioNavigationCatalog.VectorEditor:
                if (!_appSettings.Behavior.VectorEditorEnabled) return;
                var vectorRecovery = projectId is null ? null : VectorProjectRecovery.PathFor(projectId);
                if (LoadCrashRecoveryVectorProject(file, vectorRecovery, projectId) is not { } vector) return;
                window = OpenVectorEditor(vector.Project, vector.Archive);
                break;
            case ScenarioNavigationCatalog.OfficeReader:
            case ScenarioNavigationCatalog.OfficeEditor:
                var group = ReaderWindowGroup.For(this, L, _isDarkTheme);
                var draft = projectId is not null && EditorDraft.Recoverable().Any(item => item.Id == projectId)
                    ? projectId : null;
                if (entry.Kind == ScenarioNavigationCatalog.OfficeEditor)
                {
                    if (file is null && draft is null) return;
                    window = group.OpenEditor(file, draft);
                }
                else if (draft is not null)
                {
                    window = group.OpenEditor(file, draft);
                    restoreOfficeReading = true;
                }
                else window = group.Open(file);
                break;
            case FileViewerRecoveryKind:
                if (file is null) return;
                window = _fileViewerService.RestoreInternal(this, file, _isDarkTheme, _localizationService);
                break;
            case ScenarioNavigationCatalog.LyricVideo:
                if (file is null) return;
                OpenLyricVideo(file); window = _clipWindows.LastOrDefault(candidate => candidate.SourcePath == file);
                break;
            case ScenarioNavigationCatalog.BookConversion:
                OpenBookConversionUtility(); window = _bookConversionWindow;
                break;
            case ScenarioNavigationCatalog.Metadata:
                OpenMetadataUtility(); window = _metadataWindow;
                break;
            case ScenarioNavigationCatalog.Publisher:
                OpenPublisherScenario(); window = _publisherWindow;
                break;
            case ScenarioNavigationCatalog.Dictation:
                if (_dictationWindow is null) ToggleDictationDock(); window = _dictationWindow;
                break;
            default: return;
        }

        if (window is null) return;
        ApplyCrashWindowLayout(window, entry.Layout);
        if (!entry.WasVisible) window.Hide();
        if (restoreOfficeReading && window is ReaderWindow reader)
            await reader.RestoreCrashRecoveryReadingAsync().WaitAsync(TimeSpan.FromSeconds(20), _backgroundLifetime.Token);
        if (entry.Position is not { } position || !double.IsFinite(position) || position <= 0) return;
        await (window switch
        {
            UpscaleWindow upscale => upscale.RestoreRecoveryPreviewAsync(position),
            VideoEditorWindow video => video.RestoreRecoveryPreviewAsync(position),
            _ => Task.CompletedTask
        }).WaitAsync(TimeSpan.FromSeconds(20), _backgroundLifetime.Token);
    }

    internal static (VectorProject Project, string? Archive)? LoadCrashRecoveryVectorProject(
        string? archive, string? recovery, string? expectedProjectId)
    {
        if (archive is not null)
        {
            try
            {
                var saved = VectorProjectArchive.Load(archive);
                if (expectedProjectId is null || saved.Id == expectedProjectId) return (saved, archive);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or System.Text.Json.JsonException or System.Xml.XmlException or InvalidDataException)
            {
                LogCrashWorkspaceFailure("workspace_vector_archive_unavailable", error);
            }
        }
        if (recovery is null || !File.Exists(recovery)) return null;
        var draft = VectorProjectArchive.Load(recovery);
        return draft.Id == expectedProjectId ? (draft, null) : null;
    }
}
