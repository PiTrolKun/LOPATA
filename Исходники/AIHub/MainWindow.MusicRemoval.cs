using System.IO;
using System.Windows;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using MessageBox = System.Windows.MessageBox;

namespace AIHub;

public partial class MainWindow
{
    private string[] MusicRemovalRoots() => _storageSettings.Models.Locations.Select(p => p.Path)
        .Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
    private MusicModelRemovalService MusicRemovalService() => new(_imageAnalysisBundleInstallationService.LibraryStore,
        new DelegateModelUsageGuard(IsManagedModelActive));
    private bool CanRemoveMusicModel(string variation) => !_managedModelOperationActive && MusicPage.CanRemoveModels
        && MusicRemovalService().HasFiles(variation, MusicRemovalRoots());

    private async Task RemoveMusicModelAsync(string variation)
    {
        if (!CanRemoveMusicModel(variation)) return;
        var deleting = false;
        try {
            var service = MusicRemovalService();
            var roots = MusicRemovalRoots();
            // Disable other mutations while inventory/confirmation are pending as well.
            _managedModelOperationActive = true;
            MusicPage.SetModelRemovalBusy(true);
            StatusText.Text = L("Music.Models.Remove.Preparing");
            ManagedModelItemsControl.IsEnabled = false;
            var plan = await Task.Run(() => service.Preview(variation, roots));
            if (plan.Files.Count == 0 && !plan.Directories.Any(Directory.Exists)) return;
            var message = string.Format(L("Music.Models.Remove.Confirm"), MusicModelVariants.Name(variation),
                string.Join("\n", plan.Components.Select(p => "• " + p)), ComponentCardViewModel.FormatBytes(plan.Bytes));
            if (plan.Preserved.Count > 0) message += "\n\n" + L("Music.Models.Remove.Shared") + "\n"
                + string.Join("\n", plan.Preserved.Select(p => "• " + p));
            if (new ModelRemovalConfirmationDialog(this, L("Music.Models.Remove.Title"), message,
                    L("Common.Cancel"), L("Music.Models.Remove.Action")).ShowDialog() != true) return;
            deleting = true;
            StatusText.Text = L("Music.Models.Remove.Removing");
            var bytes = await Task.Run(() => service.Remove(plan));
            OwnedProcessRegistry.Log("music_model_removed", "Music.Removal", detail: $"variation={variation}; files={plan.Files.Count}; bytes={bytes}");
            MessageBox.Show(this, string.Format(L("Music.Models.Remove.Done"), ComponentCardViewModel.FormatBytes(bytes)),
                L("Music.Models.Remove.Title"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception error) {
            OwnedProcessRegistry.Log("music_model_remove_failed", "Music.Removal", detail: error.ToString());
            var key = deleting ? "Music.Models.Remove.PartialFailure" : "Music.Models.Remove.Blocked";
            MessageBox.Show(this, L(key), L("Music.Models.Remove.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally {
            _managedModelOperationActive = false; ManagedModelItemsControl.IsEnabled = true;
            StatusText.Text = "";
            MusicPage.SetModelRemovalBusy(false); RefreshManagedModels();
        }
    }
}
