using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AIHub.Services.VectorGraphics;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;

namespace AIHub.Controls;

public sealed partial class VectorGenerationControl
{
    private void OpenExample(VectorExample example)
    {
        var window = new Window { Title = LiteraryExampleTitle(example), Owner = Window.GetWindow(this), Width = 1100, Height = 800,
            MinWidth = 560, MinHeight = 450, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        AutomationProperties.SetAutomationId(window, "Vector.Example.Window");
        ApplyViewerResources(window); window.SetResourceReference(Window.BackgroundProperty, "WindowBackgroundBrush");
        var root = new Grid { Margin = new Thickness(18) }; window.Content = root;
        root.RowDefinitions.Add(new());
        for (var i = 0; i < 3; i++) root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var bitmap = VectorExamples.Image(example);
        var image = new System.Windows.Controls.Image { Source = bitmap, Stretch = Stretch.Uniform };
        AutomationProperties.SetAutomationId(image, "Vector.Example.Original");
        root.Children.Add(new VectorImageViewport(image, L("ZoomHint"), () => VectorExamples.Svg(example),
            (double)bitmap.PixelWidth / bitmap.PixelHeight, L("ZoomError")));
        var label = Text(L("Examples.OriginalPrompt")); Grid.SetRow(label, 1); root.Children.Add(label);
        var prompt = new TextBox { Text = VectorExamples.Prompt(example), IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 180, Padding = new Thickness(10) };
        prompt.SetResourceReference(TextBox.ForegroundProperty, "TextPrimaryBrush");
        prompt.SetResourceReference(TextBox.BackgroundProperty, "PanelBrush");
        AutomationProperties.SetAutomationId(prompt, "Vector.Example.Prompt"); Grid.SetRow(prompt, 2); root.Children.Add(prompt);
        var actions = new WrapPanel(); Grid.SetRow(actions, 3); root.Children.Add(actions);
        var copy = new Button { Content = L("Examples.CopyPrompt"), Margin = new Thickness(0, 4, 8, 4) };
        AutomationProperties.SetAutomationId(copy, "Vector.Example.CopyPrompt");
        copy.Click += (_, _) => { try { Clipboard.SetText(prompt.Text); } catch (System.Runtime.InteropServices.ExternalException) { label.Text = L("Examples.CopyFailed"); } };
        actions.Children.Add(copy);
        var save = new Button { Content = L("Examples.Save"), Margin = new Thickness(0, 4, 8, 4) };
        AutomationProperties.SetAutomationId(save, "Vector.Example.Save");
        save.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "SVG (*.svg)|*.svg", DefaultExt = ".svg", FileName = example.Id + ".svg" };
            if (dialog.ShowDialog(window) != true) return;
            try { using var input = VectorExamples.Open(example.Id + ".svg"); using var output = File.Create(dialog.FileName); input.CopyTo(output); }
            catch (IOException error) { label.Text = L("Error") + " " + error.Message; }
            catch (UnauthorizedAccessException error) { label.Text = L("Error") + " " + error.Message; }
        };
        actions.Children.Add(save);
        var close = new Button { Content = L("Examples.Close"), Margin = new Thickness(0, 4, 0, 4) }; close.Click += (_, _) => window.Close(); actions.Children.Add(close);
        window.Show();
    }
    private string LiteraryExampleTitle(VectorExample example) => L("Title") + " · " + L("Examples." + example.Id);
}
