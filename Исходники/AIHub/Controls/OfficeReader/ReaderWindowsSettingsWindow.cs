using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AIHub.Models;
using AIHub.Services.OfficeReader;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Orientation = System.Windows.Controls.Orientation;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace AIHub.Controls.OfficeReader;

internal sealed class ReaderWindowsSettingsWindow : Window
{
    internal ReaderWindowsSettingsWindow(Window owner, Func<string, string> text, bool dark,
        FileViewerSettings settings, Action save, string? registeredName)
    {
        Owner = owner; Icon = owner.Icon; Title = text("Reader.Windows.Formats");
        Width = 820; Height = 780; MinWidth = 650; MinHeight = 400; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Resources.MergedDictionaries.Add(new() { Source = new Uri("/AIHub;component/Controls/OfficeReader/ReaderResources.xaml", UriKind.Relative) });
        SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
        Resources["ReaderText"] = Brush(dark ? "#F1F4FA" : "#202735");
        Resources["ReaderPanel"] = Brush(dark ? "#172132" : "#FFFFFF");
        Resources["ReaderLine"] = Brush(dark ? "#3A465C" : "#CCD3DF");
        Resources["ReaderHover"] = Brush(dark ? "#293954" : "#E7EFFF");
        Foreground = (System.Windows.Media.Brush)Resources["ReaderText"]; Background = Brush(dark ? "#0D1523" : "#EDF0F6");
        AutomationProperties.SetAutomationId(this, "Office.Reader.Windows");
        var root = new DockPanel { Margin = new Thickness(18) }; Content = root;
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        header.Children.Add(new TextBlock { Text = text("Reader.Windows.Help"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 10) });
        header.Children.Add(new TextBlock { Text = text("Reader.Windows.Legend"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12) });
        var footer = new WrapPanel { Margin = new(0, 12, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var windows = new Button { Content = text("Reader.Windows.DefaultApps") };
        AutomationProperties.SetAutomationId(windows, "Office.Reader.Windows.DefaultApps");
        windows.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(registeredName is null || !settings.ReaderWindowsIntegrationEnabled ? "ms-settings:defaultapps"
                : "ms-settings:defaultapps?registeredAppUser=" + Uri.EscapeDataString(registeredName)) { UseShellExecute = true }); }
            catch { System.Windows.MessageBox.Show(this, text("Reader.Windows.SettingsFailed"), Title); }
        };
        footer.Children.Add(windows);
        var close = new Button { Content = text("Reader.Windows.Close") }; close.Click += (_, _) => Close(); footer.Children.Add(close);
        var list = new StackPanel(); root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        foreach (var extension in ReaderFileRequest.Extensions)
        {
            var card = new StackPanel { Margin = new(8) };
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            card.Children.Add(line);
            line.Children.Add(new TextBlock { Text = extension, Width = 105, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold });
            var choice = new CheckBox { IsThreeState = true, VerticalAlignment = VerticalAlignment.Center, Margin = new(5), Foreground = Foreground };
            choice.IsChecked = settings.PreferInternalByExtension.TryGetValue(extension, out var manual) ? manual : null;
            void Label() => choice.Content = text(choice.IsChecked switch { true => "Reader.Windows.Internal", false => "Reader.Windows.System", _ => "Reader.Windows.Automatic" });
            Label(); AutomationProperties.SetAutomationId(choice, "Office.Reader.Windows" + extension);
            void Changed(object sender, RoutedEventArgs args)
            {
                var old = settings.PreferInternalByExtension.TryGetValue(extension, out var value) ? (bool?)value : null;
                if (choice.IsChecked is { } selected) settings.PreferInternalByExtension[extension] = selected;
                else settings.PreferInternalByExtension.Remove(extension);
                try { save(); Label(); }
                catch
                {
                    if (old is { } previous) settings.PreferInternalByExtension[extension] = previous; else settings.PreferInternalByExtension.Remove(extension);
                    choice.Checked -= Changed; choice.Unchecked -= Changed; choice.Indeterminate -= Changed;
                    choice.IsChecked = old; Label();
                    choice.Checked += Changed; choice.Unchecked += Changed; choice.Indeterminate += Changed;
                    System.Windows.MessageBox.Show(this, text("Reader.Windows.SaveFailed"), Title);
                }
            }
            choice.Checked += Changed; choice.Unchecked += Changed; choice.Indeterminate += Changed; line.Children.Add(choice);
            var state = ReaderAssociationDiscovery.Read(extension);
            card.Children.Add(new TextBlock { Text = text("Reader.Windows.Current") + " " +
                (string.IsNullOrEmpty(state.CurrentName) ? text("Reader.Windows.Unassigned") : state.CurrentName), TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 0) });
            card.Children.Add(new TextBlock { Text = text(state.Uncertain ? "Reader.Windows.Unknown" : state.HasOtherReader ? "Reader.Windows.OtherReader" : "Reader.Windows.AutoReader"),
                TextWrapping = TextWrapping.Wrap, Opacity = .75 });
            list.Children.Add(new Border { Child = card, BorderThickness = new(0, 0, 0, 1), BorderBrush = (System.Windows.Media.Brush)Resources["ReaderLine"] });
        }
    }
}
