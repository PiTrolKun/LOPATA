using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Automation;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace AIHub;

public sealed class AboutWindow : Window
{
    public AboutWindow(Window owner, string productName, string version, Func<string, string> text, Action updates)
    {
        Owner = owner;
        Resources = owner.Resources;
        Icon = owner.Icon;
        Title = text("About.Title");
        Width = 760; Height = 640; MinWidth = 460; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "PanelBrush");
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        var root = new DockPanel { Margin = new Thickness(24) };
        Content = root;
        var close = new Button { Content = text("About.Close"), IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        close.SetResourceReference(StyleProperty, "SecondaryButtonStyle");
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Bottom); root.Children.Add(close);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        var panel = new StackPanel();
        root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        panel.Children.Add(new TextBlock { Text = productName + "  " + version, FontSize = 24, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        var links = new WrapPanel { Margin = new Thickness(0, 0, 0, 16) };
        panel.Children.Add(links);
        AddButton("About.Author", () => OpenLink("https://github.com/PiTrolKun"), "https://github.com/PiTrolKun");
        // VK geometry from Simple Icons (CC0); original terms are bundled in Licenses/texts.
        var vkIcon = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("m9.489.004.729-.003h3.564l.73.003.914.01.433.007.418.011.403.014.388.016.374.021.36.025.345.03.333.033c1.74.196 2.933.616 3.833 1.516.9.9 1.32 2.092 1.516 3.833l.034.333.029.346.025.36.02.373.025.588.012.41.013.644.009.915.004.98-.001 3.313-.003.73-.01.914-.007.433-.011.418-.014.403-.016.388-.021.374-.025.36-.03.345-.033.333c-.196 1.74-.616 2.933-1.516 3.833-.9.9-2.092 1.32-3.833 1.516l-.333.034-.346.029-.36.025-.373.02-.588.025-.41.012-.644.013-.915.009-.98.004-3.313-.001-.73-.003-.914-.01-.433-.007-.418-.011-.403-.014-.388-.016-.374-.021-.36-.025-.345-.03-.333-.033c-1.74-.196-2.933-.616-3.833-1.516-.9-.9-1.32-2.092-1.516-3.833l-.034-.333-.029-.346-.025-.36-.02-.373-.025-.588-.012-.41-.013-.644-.009-.915-.004-.98.001-3.313.003-.73.01-.914.007-.433.011-.418.014-.403.016-.388.021-.374.025-.36.03-.345.033-.333c.196-1.74.616-2.933 1.516-3.833.9-.9 2.092-1.32 3.833-1.516l.333-.034.346-.029.36-.025.373-.02.588-.025.41-.012.644-.013.915-.009ZM6.79 7.3H4.05c.13 6.24 3.25 9.99 8.72 9.99h.31v-3.57c2.01.2 3.53 1.67 4.14 3.57h2.84c-.78-2.84-2.83-4.41-4.11-5.01 1.28-.74 3.08-2.54 3.51-4.98h-2.58c-.56 1.98-2.22 3.78-3.8 3.95V7.3H10.5v6.92c-1.6-.4-3.62-2.34-3.71-6.92Z"),
            Width = 24, Height = 24, Stretch = Stretch.Uniform,
            Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 119, 255))
        };
        const string vkUrl = "https://vk.ru/lopata_ai_localhub";
        var vkButton = new Button { Content = vkIcon, Width = 44, MinWidth = 44,
            Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(0, 0, 8, 8),
            ToolTip = text("About.VkCommunity") + "\n" + vkUrl };
        vkButton.SetResourceReference(StyleProperty, "SecondaryButtonStyle");
        AutomationProperties.SetName(vkButton, text("About.VkCommunity"));
        AutomationProperties.SetAutomationId(vkButton, "About.VkCommunity");
        AutomationProperties.SetHelpText(vkButton, vkUrl);
        vkButton.Click += (_, _) => OpenLink(vkUrl);
        links.Children.Add(vkButton);
        AddButton("About.Repository", () => OpenLink("https://github.com/PiTrolKun/LOPATA/releases"), "https://github.com/PiTrolKun/LOPATA/releases");
        AddButton("Updates.Check", updates);
        foreach (var key in new[] { "Intro", "Choice", "Control" }) panel.Children.Add(Paragraph("About." + key));
        var guideHeading = Paragraph("About.GuideTitle");
        guideHeading.FontWeight = FontWeights.SemiBold;
        panel.Children.Add(guideHeading);
        foreach (var key in new[] { "Start", "Navigation", "Images", "Generation", "Music", "ImageUtility", "Shell", "Capture", "Finance", "Literary", "Import", "Sending", "Sources", "Memory", "Prompts", "Background", "Updates" })
        {
            var section = new Expander
            {
                Header = new TextBlock { Text = text("About.Guide." + key + "Title"), TextWrapping = TextWrapping.Wrap },
                Content = Paragraph("About.Guide." + key),
                IsExpanded = false,
                Margin = new Thickness(0, 0, 0, 10),
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            section.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            panel.Children.Add(section);
        }
        var details = new StackPanel { Margin = new Thickness(0, 12, 12, 0) };
        foreach (var key in new[] { "Why", "Tasks", "Hardware", "Local", "Trust", "Future", "Open" })
        {
            var heading = Paragraph("About." + key + "Title");
            heading.FontWeight = FontWeights.SemiBold; heading.Margin = new Thickness(0, 12, 0, 6);
            details.Children.Add(heading); details.Children.Add(Paragraph("About." + key));
        }
        details.Children.Add(Paragraph("About.Status"));
        var expander = new Expander { Header = new TextBlock { Text = text("About.More"), TextWrapping = TextWrapping.Wrap },
            Content = details, IsExpanded = false, Margin = new Thickness(0, 8, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        expander.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        panel.Children.Add(expander);

        TextBlock Paragraph(string key)
        {
            var block = new TextBlock { Text = text(key), TextWrapping = TextWrapping.Wrap,
                FontSize = 15, Margin = new Thickness(0, 0, 0, 12) };
            block.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            return block;
        }
        void AddButton(string key, Action action, string? tooltip = null)
        {
            var button = new Button { Content = text(key), Margin = new Thickness(0, 0, 8, 8), ToolTip = tooltip };
            button.SetResourceReference(StyleProperty, "SecondaryButtonStyle");
            button.Click += (_, _) => action(); links.Children.Add(button);
        }
        void OpenLink(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception)
            {
                System.Windows.MessageBox.Show(this, text("About.LinkFailed") + "\n\n" + url, text("About.Title"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }
}
