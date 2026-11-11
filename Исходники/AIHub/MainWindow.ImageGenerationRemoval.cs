using System.Windows;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using MessageBox = System.Windows.MessageBox;

namespace AIHub;

public partial class MainWindow
{
    private string ImageGenerationModelRoot() =>
        _storageSettings.Models.Locations.Select(location => location.Path)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path)) ?? "";

    private bool CanRemoveImageGenerationModel(ImageGenerationModel model) => !_managedModelOperationActive
        && ImageGenerationModelRemoval.HasModelFiles(ImageGenerationModelRoot(), model);

    private async Task RemoveImageGenerationModelAsync(ImageGenerationModel model)
    {
        if (!GenerationPage.CanRemoveModels || !CanRemoveImageGenerationModel(model)) return;
        var root = ImageGenerationModelRoot();
        var service = new ImageGenerationModelRemoval(_imageAnalysisBundleInstallationService.LibraryStore,
            new DelegateModelUsageGuard(id => _backgroundOperations?.IsRunning == true || IsManagedModelActive(id)));
        var deleting = false;
        try
        {
            _managedModelOperationActive = true;
            GenerationPage.SetModelRemovalBusy(true);
            ManagedModelItemsControl.IsEnabled = false;
            StatusText.Text = L("Music.Models.Remove.Preparing");
            var plan = await Task.Run(() => service.Preview(root, model.Id));
            if (!GenerationPage.IsVisible || !ManagedModelPathIdentity.SameDirectory(root, ImageGenerationModelRoot())) return;
            if (plan.Components.Length == 0)
            {
                MessageBox.Show(this, L("Generation.Remove.Unavailable"), L("Generation.Remove.Title"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var components = string.Join("\n", plan.Components.Select(component => "• " + component.Card.DisplayName));
            var message = LF("Generation.Remove.Confirm", model.Name, components, ComponentCardViewModel.FormatBytes(plan.Bytes));
            if (ImageGenerationCatalog.VideoReferenceModels.Any(candidate => candidate.Id == model.Id))
                message += "\n\n" + L("Generation.Remove.VideoReferences");
            if (plan.Preserved.Length > 0)
                message += "\n\n" + L("Music.Models.Remove.Shared") + "\n" + string.Join("\n", plan.Preserved.Select(name => "• " + name));
            if (new ModelRemovalConfirmationDialog(this, L("Generation.Remove.Title"), message,
                L("Common.Cancel"), L("Music.Models.Remove.Action")).ShowDialog() != true) return;

            deleting = true;
            StatusText.Text = L("Music.Models.Remove.Removing");
            var bytes = await Task.Run(() => service.Remove(plan));
            OwnedProcessRegistry.Log("image_model_removed", "Generation.Removal", detail: $"model={model.Id}; bytes={bytes}");
            MessageBox.Show(this, LF("Music.Models.Remove.Done", ComponentCardViewModel.FormatBytes(bytes)),
                L("Generation.Remove.Title"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception error)
        {
            OwnedProcessRegistry.Log("image_model_remove_failed", "Generation.Removal", detail: error.ToString());
            var key = deleting ? "Music.Models.Remove.PartialFailure" : "Music.Models.Remove.Blocked";
            MessageBox.Show(this, L(key), L("Generation.Remove.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _managedModelOperationActive = false;
            ManagedModelItemsControl.IsEnabled = true;
            StatusText.Text = "";
            GenerationPage.SetModelRemovalBusy(false);
            if (_managedModelsInitialized) RefreshManagedModels();
        }
    }
}
