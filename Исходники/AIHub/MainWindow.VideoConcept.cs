using System.IO;
using System.Windows;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VideoPreparation;

namespace AIHub;

public partial class MainWindow
{
    private VideoConceptAssistant? _videoConceptAssistant;
    private void ConfigureVideoConcept(VideoPreparationControl control)
    {
        _videoConceptAssistant ??= new(_userContextService);
        _videoConceptAssistant.GpuAdmission = control.DecideGpuAsync;
        control.ConfigureConceptAssistant(_videoConceptAssistant, token => VideoConceptModels.DiscoverAsync(_storageSettings, token));
        control.ConfigureGeneratorPreparation(settings => PrepareVideoGenerator(control, settings));
        control.ConfigureH3Generation(_storageSettings.Models.Locations.Select(l => l.Path).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)) ?? "",
            _appSettings.ModelDownloads?.MaximumParallelConnections ?? 0);
        control.ConfigureH3Tray(() => TryHideToTray(true));
        control.ConfigureH3Editor(async path => { OpenVideoEditorUtility(); if (_videoEditor is not null) await _videoEditor.ImportGeneratedAsync(path); });
        control.ConfigureReferenceDrawing(_storageSettings.Models.Locations.Select(l => l.Path).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)) ?? "",
            _appSettings.ModelDownloads?.MaximumParallelConnections ?? 0, () => _userProfile.DisplayName, OpenManualReferenceDrawing);
    }

    private VideoPreparationControl OpenVideoConcept()
    {
        ShowBackgroundScenarioPage(WorkStartPage);
        foreach (var page in new FrameworkElement[] { FinancialPage, CapturePage, GenerationPage, ImageUtilityPage, MusicPage, VectorPage, RagBooksPageHost })
            page.Visibility = Visibility.Collapsed;
        ScenarioNavigationPage.Visibility = Visibility.Visible;
        if (!ScenarioNavigationPage.IsVideoPreparation)
        {
            ScenarioNavigationPage.SelectDirection(ScenarioNavigationCatalog.Creation);
            ScenarioNavigationPage.OpenScenario(ScenarioNavigationCatalog.VideoGeneration);
        }
        var control = ScenarioNavigationPage.VideoPreparation ?? throw new IOException("VideoPrep.AiUnavailable");
        ConfigureVideoConcept(control); return control;
    }

    private void RegisterVideoConceptBackgroundOperations()
    {
        _backgroundOperations!.Register(VideoConceptWork.Kind, (state, token) => OpenVideoConcept().ResumeConceptAsync(state, token));
        _backgroundOperations.Register(VideoH3Job.Kind, (state, token) => OpenVideoConcept().ResumeH3Async(state, token));
        _backgroundOperations.Register(VideoTranslationProtocol.Kind, (state, token) => OpenVideoConcept().ResumeConceptAsync(state, token));
        _backgroundOperations.Register(VideoDevelopment.Kind, (state, token) => OpenVideoConcept().ResumeConceptAsync(state, token));
        _backgroundOperations.Register(VideoDevelopment.TranslationKind, (state, token) => OpenVideoConcept().ResumeConceptAsync(state, token));
        _backgroundOperations.Register(VideoWorld.Kind, (state, token) => OpenVideoConcept().ResumeWorldAsync(state, token));
        _backgroundOperations.Register(VideoWorld.CardKind, (state, token) => OpenVideoConcept().ResumeConceptAsync(state, token));
        _backgroundOperations.Register(VideoWorld.TranslationKind, (state, token) => OpenVideoConcept().ResumeConceptAsync(state, token));
        _backgroundOperations.Register(VideoReferenceWork.Kind, (state, token) => OpenVideoConcept().ResumeReferencePlanAsync(state, token));
        _backgroundOperations.Register(VideoReferenceImageWork.Kind, (state, token) => OpenVideoConcept().ResumeReferenceImageAsync(state, token));
        _backgroundOperations.Register(VideoShots.Kind, (state, token) => OpenVideoConcept().ResumeShotsAsync(state, token));
        _backgroundOperations.Register(VideoShots.TranslationKind, (state, token) => OpenVideoConcept().ResumeShotsAsync(state, token));
        _backgroundOperations.Register(VideoConceptWarmup.Kind, (state, token) => OpenVideoConcept().ResumeWarmAsync(state, token));
        _backgroundOperations.Register(VideoPreparationControl.ExportKind, (state, token) => OpenVideoConcept().ResumeExportAsync(state, token));
        _backgroundOperations.Changed += () =>
        {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(() =>
            { ScenarioNavigationPage.VideoPreparation?.RefreshConceptOperationState(); ScenarioNavigationPage.VideoPreparation?.RefreshRuntimeStatus(); ScenarioNavigationPage.VideoPreparation?.RefreshH3OperationState(); });
        };
    }

    private bool ViewVideoConceptResult(BackgroundOperationNotice notice)
    {
        var saved = _backgroundOperations!.LoadResult(notice.Id); if (saved is null) return false;
        RestoreFromTray();
        if (saved.Kind == VideoH3Job.Kind) return OpenVideoConcept().ShowH3Result(saved);
        if (saved.Kind == VideoConceptWarmup.Kind) { OpenVideoConcept().RefreshRuntimeStatus(); return true; }
        if (saved.Kind == VideoReferenceWork.Kind) return OpenVideoConcept().ShowReferencePlanResult(saved);
        if (saved.Kind == VideoPreparationControl.ExportKind) return OpenVideoConcept().ShowExportResult(saved);
        if (saved.Kind == VideoReferenceImageWork.Kind) return OpenVideoConcept().ShowReferenceImageResult(saved);
        if (saved.Kind is VideoShots.Kind or VideoShots.TranslationKind) return OpenVideoConcept().ShowShotsResult(saved);
        return saved.Kind == VideoWorld.Kind ? OpenVideoConcept().ShowWorldResult(saved) : OpenVideoConcept().ShowConceptResult(saved);
    }
}
