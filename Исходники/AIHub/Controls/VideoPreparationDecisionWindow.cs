using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace AIHub.Controls;

/// <summary>Owned, themed, keyboard-accessible choice. Closing the window always means cancel.</summary>
public sealed class VideoPreparationDecisionWindow : Window
{
    public string Choice { get; private set; } = "cancel";
    public VideoPreparationDecisionWindow(Window? owner, Func<string, string> localize, string titleKey, string message,
        params (string Key, string Value)[] choices)
    {
        string T(string key) => localize("VideoPrep." + key);
        Owner = owner; Title = T(titleKey); ShowInTaskbar = false; Width = Math.Min(640, SystemParameters.WorkArea.Width - 40);
        MinWidth = 320; MaxHeight = SystemParameters.WorkArea.Height - 40; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/VideoPreparationResources.xaml", UriKind.Relative) });
        foreach (var key in new[] { "WindowBackgroundBrush", "PanelBrush", "LineBrush", "TextPrimaryBrush", "TextSecondaryBrush",
            "AccentBrush", "SecondaryButtonBackgroundBrush", "StepBadgeBrush", "UiBodyFontSize" })
            if (owner?.TryFindResource(key) is { } value) Resources[key] = value;
        foreach (var (key, color) in new[] { ("WindowBackgroundBrush", Colors.White), ("PanelBrush", Colors.WhiteSmoke),
            ("LineBrush", Colors.Gray), ("TextPrimaryBrush", Colors.Black), ("TextSecondaryBrush", Colors.DimGray),
            ("AccentBrush", Colors.RoyalBlue), ("SecondaryButtonBackgroundBrush", Colors.WhiteSmoke), ("StepBadgeBrush", Colors.LightBlue) })
            if (!Resources.Contains(key)) Resources[key] = new SolidColorBrush(color);
        if (!Resources.Contains("UiBodyFontSize")) Resources["UiBodyFontSize"] = 14d;
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush"); SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        SetResourceReference(FontSizeProperty, "UiBodyFontSize");
        AutomationProperties.SetAutomationId(this, "VideoPrep.Decision");
        SourceInitialized += (_, _) => Services.WindowTitleBarThemeService.Apply(this, Resources["WindowBackgroundBrush"] is SolidColorBrush b && b.Color.R < 128);
        var root = new DockPanel { Margin = new Thickness(20) };
        var buttons = new WrapPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        foreach (var (key, value) in choices)
        {
            var button = MusicWishUi.Button(T(key), "VideoPrep.Decision." + value, () => { Choice = value; DialogResult = true; });
            button.Padding = new Thickness(12, 8, 12, 8);
            if (button.Content is TextBlock label) label.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground") { Source = button });
            if (value == "cancel") button.IsCancel = true;
            buttons.Children.Add(button);
        }
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap } });
        Content = root;
    }
}
