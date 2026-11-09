using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using Microsoft.Win32;
using Button = System.Windows.Controls.Button;
using ListBox = System.Windows.Controls.ListBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace AIHub.Controls;

internal sealed class CollectionFoldersWindow : Window
{
    private readonly ListBox locations = new() { MinHeight = 160, Margin = new(0, 12, 0, 12) };
    internal List<string> Folders => locations.Items.Cast<string>().ToList();

    internal CollectionFoldersWindow(IEnumerable<string> folders, Func<string, string> localize)
    {
        MusicWishUi.PrepareWindow(this, localize("Collection.Folders"), "Collection.Folders.Window");
        Width = 680;
        Height = 400;
        MinWidth = 420;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        AutomationProperties.SetAutomationId(this, "Collection.Folders.Window");
        AutomationProperties.SetAutomationId(locations, "Collection.Folders.List");
        foreach (var path in folders.Distinct(StringComparer.OrdinalIgnoreCase)) locations.Items.Add(path);
        var panel = new DockPanel { Margin = new(20) };
        var help = new TextBlock { Text = localize("Collection.FoldersHelp"), TextWrapping = TextWrapping.Wrap };
        help.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        DockPanel.SetDock(help, Dock.Top);
        panel.Children.Add(help);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Button AddButton(string key, string id, Action action)
        {
            var button = new Button { Content = localize(key), Margin = new(5, 0, 0, 0), Padding = new(12, 6, 12, 6) };
            AutomationProperties.SetAutomationId(button, id);
            button.Click += (_, _) => action();
            buttons.Children.Add(button);
            return button;
        }
        AddButton("Collection.AddFolder", "Collection.Folders.Add", () =>
        {
            var dialog = new OpenFolderDialog { Title = localize("Collection.AddFolder"), Multiselect = true };
            if (dialog.ShowDialog(this) != true) return;
            foreach (var path in dialog.FolderNames)
                if (!Folders.Contains(path, StringComparer.OrdinalIgnoreCase)) locations.Items.Add(path);
        });
        AddButton("Collection.RemoveFolder", "Collection.Folders.Remove", () =>
        {
            if (locations.SelectedItem is string selected) locations.Items.Remove(selected);
        });
        AddButton("Collection.SaveFolders", "Collection.Folders.Save", () => { DialogResult = true; });
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        panel.Children.Add(locations);
        Content = panel;
    }
}
