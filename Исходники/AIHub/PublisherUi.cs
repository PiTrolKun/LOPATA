using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;

namespace AIHub;

internal static class PublisherUi
{
    public static void Prepare(Window window, Window owner, string title, double width = 1040, double height = 720)
    {
        window.Owner = owner; window.Title = title; window.ShowInTaskbar = false;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        window.Width = Math.Min(width, SystemParameters.WorkArea.Width - 32);
        window.Height = Math.Min(height, SystemParameters.WorkArea.Height - 32);
        window.MinWidth = Math.Min(460, window.Width); window.MinHeight = Math.Min(380, window.Height);
        window.Resources.MergedDictionaries.Add(owner.Resources);
        window.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/AIHub;component/Controls/SettingsResources.xaml") });
        window.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/AIHub;component/Controls/PublisherResources.xaml") });
        window.SetResourceReference(Window.BackgroundProperty, "PanelBrush");
        window.SetResourceReference(Window.ForegroundProperty, "TextPrimaryBrush");
        window.SetResourceReference(Window.FontSizeProperty, "UiBodyFontSize");
        void ApplyTitleBar(object? sender, EventArgs args)
        {
            if (window.Background is System.Windows.Media.SolidColorBrush brush)
            {
                var color = brush.Color;
                Services.WindowTitleBarThemeService.Apply(window, .2126 * color.R + .7152 * color.G + .0722 * color.B < 128);
            }
        }
        window.SourceInitialized += ApplyTitleBar;
        var background = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(Window.BackgroundProperty, typeof(Window));
        background.AddValueChanged(window, ApplyTitleBar);
        window.Closed += (_, _) => background.RemoveValueChanged(window, ApplyTitleBar);
    }
    public static Button Button(string text, string id, Action action)
    {
        var button = new Button { Content = Text(text), Margin = new(4), Padding = new(12, 8, 12, 8) };
        AutomationProperties.SetAutomationId(button, id); button.Click += (_, _) => action(); return button;
    }
    public static TextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new(0, 5, 0, 5) };
    public static TextBox Input(string id, string name, string value = "")
    {
        var box = new TextBox { Text = value, Margin = new(0, 4, 0, 12) };
        AutomationProperties.SetAutomationId(box, id); AutomationProperties.SetName(box, name); return box;
    }
    public static WrapPanel Buttons() => new() { Orientation = Orientation.Horizontal, Margin = new(0, 8, 0, 0) };
    public static void StyleSecret(PasswordBox box)
    {
        box.SetResourceReference(PasswordBox.BackgroundProperty, "InputBrush");
        box.SetResourceReference(PasswordBox.ForegroundProperty, "TextPrimaryBrush");
        box.SetResourceReference(PasswordBox.CaretBrushProperty, "TextPrimaryBrush");
        box.SetResourceReference(PasswordBox.BorderBrushProperty, "LineBrush");
    }
    public static Grid Frame(out StackPanel header, out ScrollViewer body, out StackPanel footer)
    {
        var root = new Grid { Margin = new(18) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new());
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        header = new(); body = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; footer = new();
        Grid.SetRow(body, 1); Grid.SetRow(footer, 2); root.Children.Add(header); root.Children.Add(body); root.Children.Add(footer);
        return root;
    }
    public static bool Confirm(Window owner, Func<string, string> text, string question)
    {
        var dialog = new Window(); Prepare(dialog, owner, text("Publisher.Title"), 580, 420);
        dialog.Content = Frame(out _, out var body, out var footer);
        body.Content = Text(question);
        var buttons = Buttons(); footer.Children.Add(buttons);
        var yes = Button(text("Dialog.Yes"), "Publisher.Confirm.Yes", () => dialog.DialogResult = true);
        var no = Button(text("Dialog.No"), "Publisher.Confirm.No", () => dialog.DialogResult = false);
        no.IsDefault = no.IsCancel = true; buttons.Children.Add(yes); buttons.Children.Add(no);
        return dialog.ShowDialog() == true;
    }
    public static void OpenLink(string url) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
}
