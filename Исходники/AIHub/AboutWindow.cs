using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        AddButton("About.Repository", () => OpenLink("https://github.com/PiTrolKun/LOPATA"), "https://github.com/PiTrolKun/LOPATA");
        AddButton("Updates.Check", updates);
        foreach (var key in new[] { "Intro", "Choice", "Control" }) panel.Children.Add(Paragraph("About." + key));
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
