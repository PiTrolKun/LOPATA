using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AIHub.Services;
using AIHub.Services.VisualScenario;

namespace AIHub.Controls.VisualScenario;

internal static class VisualSenseNovaHint
{
    internal static Popup? Show(FrameworkElement target, Func<string, string> localize, string selected, string other, bool artist)
    {
        if (selected != VisualSenseNova.Id || other == VisualSenseNova.Id || !target.IsVisible) return null;
        var text = localize(artist ? "Visual.SenseNovaSuggestAdvisor" : "Visual.SenseNovaSuggestArtist");
        var popup = new Popup { PlacementTarget = target, Placement = PlacementMode.Bottom, StaysOpen = false };
        var panel = new StackPanel { MaxWidth = 330 };
        panel.Children.Add(MusicWishUi.Text(text));
        panel.Children.Add(MusicWishUi.Button(localize("Common.Close"), "Visual.SenseNovaHint.Close", () => popup.IsOpen = false));
        var border = new Border { Child = panel, Padding = new(12), BorderThickness = new(1) };
        border.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        popup.Child = border;
        popup.IsOpen = true;
        return popup;
    }
}
