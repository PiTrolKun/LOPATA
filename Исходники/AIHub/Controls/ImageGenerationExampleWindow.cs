using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Image = System.Windows.Controls.Image;
using Clipboard = System.Windows.Clipboard;

namespace AIHub.Controls;

public sealed class ImageGenerationExampleWindow : Window
{
    public ImageGenerationExampleWindow(GenerationExample example, Func<string, string> localize)
    {
        var language = localize("Generation.Examples.Language");
        string L(string key) => localize("Generation.Examples." + key);
        Title = ImageGenerationCatalog.DisplayName(example.ModelId) + " · " + L(example.TitleKey);
        Width = 1100; Height = 800; MinWidth = 560; MinHeight = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (System.Windows.Application.Current?.MainWindow is { } main && main != this) Resources.MergedDictionaries.Add(main.Resources);
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/SettingsResources.xaml", UriKind.Relative) });
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        var root = new Grid { Margin = new Thickness(18) }; Content = root;
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var image = new Image { Source = ImageGenerationExamples.Image(example), Stretch = Stretch.Uniform };
        AutomationProperties.SetAutomationId(image, "Generation.Example.Original");
        root.Children.Add(image);
        var label = new TextBlock { Text = L("Prompt") + " · " + (language == example.SourceLanguage ? L("Original") : L("Translation")), Margin = new Thickness(0, 12, 0, 6) };
        Grid.SetRow(label, 1); root.Children.Add(label);
        var prompt = new TextBox { Text = example.Prompt(language), IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 180, Padding = new Thickness(10) };
        AutomationProperties.SetAutomationId(prompt, "Generation.Example.Prompt"); Grid.SetRow(prompt, 2); root.Children.Add(prompt);
        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) }; Grid.SetRow(actions, 3); root.Children.Add(actions);
        var copy = new Button { Content = L("CopyPrompt"), Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetAutomationId(copy, "Generation.Example.CopyPrompt");
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(prompt.Text); }
            catch (System.Runtime.InteropServices.ExternalException) { label.Text = L("CopyFailed"); }
        };
        actions.Children.Add(copy);
        var close = new Button { Content = L("Close") }; close.Click += (_, _) => Close(); actions.Children.Add(close);
    }
}
