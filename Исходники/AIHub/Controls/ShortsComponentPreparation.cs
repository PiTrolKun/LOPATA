using System.Windows;
using AIHub.Controls.VideoEditor;
using AIHub.Models;
using AIHub.Services.Shorts;

namespace AIHub.Controls;

internal static class ShortsComponentPreparation
{
    internal static bool Show(Window owner, Func<string, string> localize, string root, int connections, CancellationToken token)
    {
        var cards = ShortsComponents.Cards(root);
        var names = cards.ToDictionary(card => card.ModelArtifactId, card => card.DisplayName);
        var details = string.Join("\n", cards.Select(card => card.DisplayName + " · " + ComponentCardViewModel.FormatBytes(card.TotalBytes)));
        var window = new VideoPreparationWindow(owner, localize, localize("Shorts.Title"),
            (download, progress, ct) => ShortsComponents.PrepareAsync(root, connections, download, progress, ct),
            token, details, names);
        return BackgroundUserDecision.ShowDialog(window, owner, token) == true;
    }
}
