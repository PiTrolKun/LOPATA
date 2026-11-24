using System.IO;
using System.Windows;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.LivingArcade;

namespace AIHub;

public partial class MainWindow
{
    private CrashWorkspacePage CaptureCrashWorkspacePage()
    {
        var settings = SettingsPage.Visibility == Visibility.Visible;
        var page = settings ? _settingsReturnPage : FindVisiblePageBeforeSettings();
        if (page == LiteraryPage)
            return new(ScenarioNavigationCatalog.Literary, Project: LiteraryPage.CurrentWorkspaceDirectory, SettingsOpen: settings);
        if (page == ChoiceScenarioPage)
            return new(nameof(ChoiceScenarioPage), Project: _activeResumableSession?.SessionId, SettingsOpen: settings);
        if (page == ImageAnalysisWorkspacePage || page == ImageAnalysisBundleConfirmationPage || page == ImageAnalysisBundleSelectorPage)
            return new(ScenarioNavigationCatalog.Images, SettingsOpen: settings);
        if (page == ProfilePage || page == ProfileReminderPage || page == SetupPage)
            return new(page.Name, SettingsOpen: settings);
        if (page != WorkStartPage)
            return new(nameof(WelcomePage), SettingsOpen: settings);

        var id = CapturePage.Visibility == Visibility.Visible ? ScenarioNavigationCatalog.Capture
            : GenerationPage.Visibility == Visibility.Visible ? ScenarioNavigationCatalog.ImageGeneration
            : VectorPage.Visibility == Visibility.Visible ? ScenarioNavigationCatalog.VectorGeneration
            : ImageUtilityPage.Visibility == Visibility.Visible ? ScenarioNavigationCatalog.ImageUtility
            : MusicPage.Visibility == Visibility.Visible ? ScenarioNavigationCatalog.Music
            : FinancialPage.Visibility == Visibility.Visible ? ScenarioNavigationCatalog.Finance
            : RagBooksPageHost.Visibility == Visibility.Visible ? ScenarioNavigationCatalog.RagBooks
            : ScenarioNavigationPage.RecoveryScenarioId ?? nameof(WorkStartPage);
        var project = id switch
        {
            ScenarioNavigationCatalog.LetItOut => ScenarioNavigationPage.LetItOutChat?.SessionId,
            ScenarioNavigationCatalog.SongClip => ScenarioNavigationPage.SongClip?.RecoveryProjectId,
            ScenarioNavigationCatalog.VisualScenario => ScenarioNavigationPage.VisualWorkspace?.RecoveryProjectId,
            ScenarioNavigationCatalog.AnimatedAssets => ScenarioNavigationPage.AnimatedAssetsWorkspace?.RecoveryPath,
            ScenarioNavigationCatalog.Music => MusicPage.RecoveryProjectId,
            ScenarioNavigationCatalog.VideoGeneration => ScenarioNavigationPage.VideoPreparation?.Session.Project.Id,
            _ => null
        };
        var file = id switch
        {
            ScenarioNavigationCatalog.LetItOut => _letItOutCorePath,
            ScenarioNavigationCatalog.VideoGeneration => ScenarioNavigationPage.VideoPreparation?.Session.ArchivePath,
            _ => null
        };
        return new(id, ScenarioNavigationPage.SelectedDirectionId, project, settings, ScenarioNavigationPage.RecoveryScrollOffset, file);
    }

    private async Task RestoreCrashWorkspacePageAsync(CrashWorkspacePage page)
    {
        switch (page.Id)
        {
            case nameof(WelcomePage): break;
            case nameof(SetupPage):
                OpenSetupWindow(regeneratePassport: false);
                break;
            case nameof(ProfilePage):
                LoadProfileIntoControls(); ShowProfilePage();
                break;
            case nameof(ProfileReminderPage):
                ShowProfileReminderPage();
                break;
            case ScenarioNavigationCatalog.Literary:
                SelectLiteraryScenarioButton_Click(this, new RoutedEventArgs());
                if (page.Project is { } literary && Path.IsPathFullyQualified(literary) && Directory.Exists(literary))
                    LiteraryPage.RestoreWorkspace(literary);
                break;
            case ScenarioNavigationCatalog.Images:
                ShowImageAnalysisBundleSelector(refreshHardware: false);
                break;
            case nameof(ChoiceScenarioPage):
                if (Guid.TryParse(page.Project, out _) && _sessionArchiveService.Load(_storageSettings, page.Project!) is { } session
                    && session.Status != ResumableSessionStatuses.Unavailable && !session.Core.PendingCoreRequest && session.Executor is null)
                    await ResumeScenarioSessionAsync(session, requestExecutorDownload: false);
                else
                {
                    ShowWorkStartPage();
                    ScenarioNavigationPage.OpenScenario(ScenarioNavigationCatalog.Sandbox);
                }
                break;
            default:
                ShowWorkStartPage();
                if (page.Direction is { } direction) ScenarioNavigationPage.SelectDirection(direction);
                switch (page.Id)
                {
                    case nameof(WorkStartPage): break;
                    case ScenarioNavigationCatalog.Music:
                        OpenMusicScenario();
                        MusicPage.RestoreRecoveryProject(page.Project);
                        break;
                    case ScenarioNavigationCatalog.SongClip: OpenSongClip().RestoreRecoveryProject(page.Project); break;
                    case ScenarioNavigationCatalog.Shorts: OpenShorts(); break;
                    case ScenarioNavigationCatalog.VisualScenario:
                        var visual = RevealVisualScenario();
                        if (page.Project is { } projectId) visual.RestoreProject(projectId);
                        break;
                    case ScenarioNavigationCatalog.AnimatedAssets:
                        var animation = ShowAnimatedAssets();
                        if (page.Project is { } draft && File.Exists(draft))
                            await animation.OpenRecoveryAsync(draft);
                        break;
                    case ScenarioNavigationCatalog.VideoGeneration:
                        OpenVideoConcept().RestoreRecoveryProject(page.Project, page.File);
                        break;
                    case ScenarioNavigationCatalog.LetItOut:
                        if (page.File is { } model && Path.IsPathFullyQualified(model) && File.Exists(model))
                            _letItOutCorePath = model;
                        var chat = RevealLetItOutChat();
                        if (Guid.TryParse(page.Project, out _)) chat.OpenConversation(page.Project!);
                        break;
                    case ScenarioNavigationCatalog.LivingArcade:
                        if (_backgroundOperations is { } background && LivingArcadePendingWork.Find(background) is { } arcade)
                            RevealLivingArcade(arcade.Paths).ShowPendingOperation(arcade.State);
                        break;
                    default: ScenarioNavigationPage.OpenScenario(page.Id); break;
                }
                ScenarioNavigationPage.RestoreRecoveryScroll(page.Scroll);
                break;
        }
        if (page.SettingsOpen) ShowSettingsPage();
    }
}
