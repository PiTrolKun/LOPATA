using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls;

internal static class PromptDialogUi
{
    public static void Configure(Window window, Window? owner, string title)
    {
        window.Title = title;
        window.Owner = owner;
        window.Width = 900;
        window.Height = 700;
        window.MinWidth = 540;
        window.MinHeight = 420;
        window.FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        window.SetResourceReference(Window.FontSizeProperty, "UiBodyFontSize");
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (owner is not null) window.Resources.MergedDictionaries.Add(owner.Resources);
        window.SetResourceReference(Window.BackgroundProperty, "PanelBrush");
        window.SetResourceReference(Window.ForegroundProperty, "TextPrimaryBrush");
        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; window.Close(); }
        };
    }

    public static Button Button(string label, Action click, bool primary = false)
    {
        var button = new Button { Content = label, MinWidth = 75, MinHeight = 34,
            Margin = new Thickness(4), Padding = new Thickness(10, 4, 10, 4) };
        button.SetResourceReference(FrameworkElement.StyleProperty, primary ? "PrimaryButtonStyle" : "SecondaryButtonStyle");
        button.Click += (_, _) => click();
        return button;
    }

    public static TextBox TextArea(string text) => new()
    {
        Text = text, AcceptsReturn = true, AcceptsTab = true, IsUndoEnabled = true,
        TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(9),
        MinHeight = 70
    };

    public static void Label(FrameworkElement element, string label)
    {
        element.ToolTip = label;
        AutomationProperties.SetName(element, label);
    }

    public static bool Confirm(Window owner, string message, string title) =>
        System.Windows.MessageBox.Show(owner, message, title, MessageBoxButton.YesNo,
            MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    public static string Error(Exception ex, Func<string, string> localize) =>
        localize(ex.Message.StartsWith("PromptPairs.", StringComparison.Ordinal)
            ? ex.Message : "PromptPairs.StorageError");

    public static string? EditWishes(Window? owner, string text, Func<string, string> localize, string? title = null)
    {
        var window = new Window();
        Configure(window, owner, title ?? localize("ImageAnalysis.Workspace.Settings.Wishes"));
        window.Width = 680; window.Height = 420;
        var root = new DockPanel { Margin = new Thickness(16) };
        var area = TextArea(text);
        Label(area, window.Title);
        var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var saved = false;
        actions.Children.Add(Button(localize("PromptPairs.Save"), () => { saved = true; window.DialogResult = true; }, true));
        actions.Children.Add(Button(localize("Common.Cancel"), window.Close));
        DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions); root.Children.Add(area);
        window.Content = root;
        window.Closing += (_, e) =>
        {
            if (!saved && area.Text != text && !Confirm(window, localize("PromptPairs.Discard"), window.Title)) e.Cancel = true;
        };
        return window.ShowDialog() == true ? area.Text : null;
    }
}
