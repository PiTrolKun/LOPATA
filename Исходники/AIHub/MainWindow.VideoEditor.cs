using System.IO;
using System.Text.Json;
using AIHub.Controls.VideoEditor;
using AIHub.Services;
using AIHub.Services.VideoEditor;

namespace AIHub;

public partial class MainWindow
{
    private readonly SemaphoreSlim _videoFileGate = new(1);
    internal async void AcceptVideoFile(VideoFileRequest request)
    {
        await _videoFileGate.WaitAsync();
        try {
            request = request.Validate(); StatusText.Text = L("VideoEditor.OpeningArchive");
            if (Path.GetExtension(request.Path).Equals(".lopataAIVid", StringComparison.OrdinalIgnoreCase))
            {
                OpenFromSecondInstance();
                ShowWorkStartPage();
                if (WorkStartPage.Visibility != System.Windows.Visibility.Visible) return;
                foreach (var page in new System.Windows.FrameworkElement[] { FinancialPage, CapturePage, GenerationPage, ImageUtilityPage, MusicPage, VectorPage, RagBooksPageHost })
                    page.Visibility = System.Windows.Visibility.Collapsed;
                ScenarioNavigationPage.Visibility = System.Windows.Visibility.Visible;
                ScenarioNavigationPage.OpenScenario(ScenarioNavigationCatalog.VideoGeneration);
                await ScenarioNavigationPage.VideoPreparation!.OpenPathWhenReadyAsync(request.Path, ApplicationBackgroundOperations.ExitToken);
                RefreshScenarioNavigationState(); return;
            }
            if (_videoEditor is {} open && await Task.Run(() => VideoProjectArchive.DescribeId(request.Path)) == open.ProjectId) {
                open.Show(); if (open.WindowState == System.Windows.WindowState.Minimized) open.WindowState = System.Windows.WindowState.Normal; open.Activate(); return;
            }
            var project = await Task.Run(() => VideoProjectStore.Load(request.Path));
            if (_videoEditor is {} existing) {
                if (existing.ProjectId != project.Id) await existing.LoadExternalProjectAsync(project, request.Path);
                existing.Show(); if (existing.WindowState == System.Windows.WindowState.Minimized) existing.WindowState = System.Windows.WindowState.Normal; existing.Activate();
            }
            else ShowVideoEditor(project, request.Path);
        }
        catch (Exception error)
        {
            VideoErrors.Report(error);
            var preparation = Path.GetExtension(request.Path).Equals(".lopataAIVid", StringComparison.OrdinalIgnoreCase);
            System.Windows.MessageBox.Show(this, L(preparation ? "VideoPrep.OpenFailed" : "VideoEditor.OpenFailed"),
                L(preparation ? "VideoPrep.Caption" : "VideoEditor.Title"), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
        finally { _videoFileGate.Release(); }
    }
    private readonly VideoWork _videoEditorWork = new();
    private VideoEditorWindow? _videoEditor;
    private void OpenVideoEditorUtility(string? path = null)
    {
        if (_videoEditor is not null) { if (path is not null && VideoProjectStore.Load(path).Id != _videoEditor.ProjectId) _videoEditor.OpenProjectPath(path); _videoEditor.Show(); _videoEditor.Activate(); return; }
        VideoProject? project = path is null ? null : VideoProjectStore.Load(path);
        ShowVideoEditor(project, path);
    }
    private void ShowVideoEditor(VideoProject? project, string? path)
    {
        var window = new VideoEditorWindow(this, L, _localizationService.CurrentLanguageCode, _videoEditorWork, project, path);
        window.EnsureRuntime = EnsureApplicationStartupAsync;
        window.PrepareModels = (need, token) => PrepareVideoModelsAsync(window, need, token);
        window.EditSvgRequested += file => _ = AcceptVectorFileAsync(new(file));
        window.Closed += (_, _) => { if (_videoEditor == window) _videoEditor = null; };
        _videoEditor = window; window.Show();
    }
    private void ApplyVideoIntegration()
    {
        if (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name != typeof(MainWindow).Assembly.GetName().Name || Environment.ProcessPath is not {} executable) return;
        new VideoShellIntegration(executable).Apply(true, L("VideoEditor.FileType"));
        new VideoShellIntegration(executable, preparation: true).Apply(true, L("VideoPrep.FileType"));
    }
    private void RegisterVideoEditorOperations()
    {
        _backgroundOperations!.Register(VideoWork.Kind, async (state, token) => {
            var request = state.Input.Deserialize<VideoWorkRequest>() ?? throw new InvalidDataException("VideoEditor.InvalidProject");
            OpenVideoEditorUtility(request.ProjectPath); _videoEditor?.FollowOperation(request.Id);
            if (_videoEditor is {} editor && !await editor.PrepareRestoredWorkAsync(request, token)) throw new OperationCanceledException(token);
            var result = await _videoEditorWork.ResumeAsync(state, token);
            if (_videoEditor is {} window && result.EndsWith(".lopataVideo", StringComparison.OrdinalIgnoreCase)) await window.AcceptOperationResultAsync(result);
        });
    }
}
