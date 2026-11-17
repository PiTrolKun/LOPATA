using System.Windows;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VisualScenario;
using MessageBox = System.Windows.MessageBox;

namespace AIHub.Controls.VisualScenario;

internal static class VisualModelRemoval
{
    internal static async Task RemoveAsync(Window owner, Func<string, string> localize, VisualComponents components,
        string id, bool artist, Func<Task> retire)
    {
        var title = localize("Visual.RemoveModel");
        var deleting = false;
        try
        {
            var plan = await Task.Run(() => components.ReviewRemoval(id, artist));
            if (plan is null)
            {
                MessageBox.Show(owner, localize("Generation.Remove.Unavailable"), title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var message = string.Format(localize("Generation.Remove.Confirm"), plan.Name,
                string.Join("\n", plan.Components.Select(name => "• " + name)), ComponentCardViewModel.FormatBytes(plan.Bytes));
            if (plan.Preserved.Length > 0)
                message += "\n\n" + localize("Music.Models.Remove.Shared") + "\n" + string.Join("\n", plan.Preserved);
            var confirm = new ModelRemovalConfirmationDialog(owner, title, message, localize("Common.Cancel"),
                localize("Music.Models.Remove.Action"));
            if (confirm.ShowDialog() != true) return;
            await retire();
            deleting = true;
            var bytes = await Task.Run(plan.Remove);
            MessageBox.Show(owner, string.Format(localize("Music.Models.Remove.Done"), ComponentCardViewModel.FormatBytes(bytes)),
                title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception error)
        {
            OwnedProcessRegistry.Log("visual_model_remove_failed", "Visual.Removal", detail: error.ToString());
            MessageBox.Show(owner, localize(deleting ? "Music.Models.Remove.PartialFailure" : "Music.Models.Remove.Blocked"),
                title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
