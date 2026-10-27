using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AIHub.Controls;

internal static class VideoReferenceWindowUi
{
    public static void Configure(Window window, Window? owner, string title)
    {
        window.Owner = owner; window.Title = title; window.Width = 1150; window.Height = 800;
        window.MinWidth = 760; window.MinHeight = 500; window.ShowInTaskbar = true;
        window.WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/VideoPreparationResources.xaml", UriKind.Relative) });
        foreach (var key in new[] { "WindowBackgroundBrush", "PanelBrush", "LineBrush", "TextPrimaryBrush", "TextSecondaryBrush", "AccentBrush", "SecondaryButtonBackgroundBrush", "StepBadgeBrush", "UiBodyFontSize", "UiSectionFontSize" })
            if (owner?.TryFindResource(key) is { } value) window.Resources[key] = value;
        window.SetResourceReference(Window.BackgroundProperty, "WindowBackgroundBrush");
        window.SetResourceReference(Window.ForegroundProperty, "TextPrimaryBrush");
    }
    public static TextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 6, 4, 6) };
    public static System.Windows.Controls.Button Button(string text, string id, Action action) => MusicWishUi.Button(text, "VideoPrep." + id, action);
    public static System.Windows.Controls.TextBox Editor(string text, bool readOnly = false) => new()
    { Text = text, IsReadOnly = readOnly, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(4), MinHeight = 110 };
}
