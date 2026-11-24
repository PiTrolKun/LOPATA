using AIHub.Controls.AnimatedAssets;
using AIHub.Services;
using AIHub.Services.AnimatedAssets;

namespace AIHub;

public partial class MainWindow
{
    private AnimationWorkshopControl? _animationWorkshop;

    private AnimationModelService AnimationModels() => new(_storageSettings,
        _appSettings.ModelDownloads.MaximumParallelConnections, L, () => _userProfile.DisplayName,
        new DelegateModelUsageGuard(id => _backgroundOperations?.IsRunning == true || IsManagedModelActiveOutsideVisual(id)),
        () => _localizationService.CurrentLanguageCode);

    private void OpenAnimatedAssets(bool returnToWorkshop = false)
    {
        var models = AnimationModels();
        var preparation = new AnimationPreparationControl(L,
            () => new(models.AdvisorName, models.AdvisorPresent, models.ArtistName, models.ArtistPresent, models.StorageConfigured),
            AnimationModelService.PrepareBaseAsync, models.DownloadMissingModelsAsync);
        preparation.ContinueRequested += () => ShowAnimatedAssets(models);
        ScenarioNavigationPage.ShowAnimatedAssetsPreparation(preparation, returnToWorkshop);
    }

    private AnimationWorkshopControl ShowAnimatedAssets(AnimationModelService? models = null)
    {
        ShowBackgroundScenarioPage(WorkStartPage);
        PrepareScenarioNavigation(ScenarioNavigationCatalog.AnimatedAssets);
        if (_animationWorkshop is null)
        {
            _animationWorkshop = new AnimationWorkshopControl(L, models ?? AnimationModels());
            _animationWorkshop.PreparationRequested += () => OpenAnimatedAssets(returnToWorkshop: true);
        }
        if (models is not null) _animationWorkshop.UpdateModels(models);
        ScenarioNavigationPage.ShowAnimatedAssets(_animationWorkshop);
        return _animationWorkshop;
    }
}
