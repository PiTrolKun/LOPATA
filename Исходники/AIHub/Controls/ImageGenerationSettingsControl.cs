using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Models;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using UserControl = System.Windows.Controls.UserControl;

namespace AIHub.Controls;

public sealed class ImageGenerationSettingsControl : UserControl
{
    private ImageGenerationSettings _settings = new();
    private Func<string, string> _text = key => key;
    public event Action? Changed;
    public void Configure(ImageGenerationSettings settings, Func<string, string> text)
    {
        _settings = settings; _text = text;
        var panel = new StackPanel { Margin = new Thickness(16) };
        var label = new TextBlock { Text = text("Generation.OutputFolder"), TextWrapping = TextWrapping.Wrap };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush"); panel.Children.Add(label);
        var path = new TextBox { Text = settings.Folder, IsReadOnly = true, Margin = new Thickness(0, 8, 0, 8) };
        AutomationProperties.SetAutomationId(path, "Generation.OutputFolder"); panel.Children.Add(path);
        var choose = new Button { Content = text("Generation.ChooseFolder"), HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Padding = new Thickness(14, 8, 14, 8) };
        AutomationProperties.SetAutomationId(choose, "Generation.ChooseFolder");
        choose.Click += (_, _) => { if (SelectFolder(Window.GetWindow(this), _settings, _text)) { Changed?.Invoke(); Configure(_settings, _text); } };
        panel.Children.Add(choose); Content = panel;
    }
    public static bool SelectFolder(Window? owner, ImageGenerationSettings settings, Func<string, string> text)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = text("Generation.ChooseFolder"), Multiselect = false };
        if (System.IO.Directory.Exists(settings.Folder)) dialog.InitialDirectory = settings.Folder;
        if (dialog.ShowDialog(owner) != true) return false;
        settings.Folder = dialog.FolderName; return true;
    }
}
