using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using Image = System.Windows.Controls.Image;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace AIHub.Controls;

public sealed partial class ImageGenerationControl
{
    private void RenderSelection(StackPanel panel)
    {
        panel.Children.Add(Text(L("ChooseModel")));
        panel.Children.Add(Text(L("Examples.TestNote")));
        foreach (var model in ImageGenerationCatalog.Manifest.Models)
        {
            var row = new Grid { Margin = new Thickness(0, 8, 0, 16) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var description = new StackPanel(); description.Children.Add(Text(model.Name, true));
            description.Children.Add(Text(L("Model." + model.Id)));
            description.Children.Add(Text(L("Card." + model.Id + ".Pros")));
            description.Children.Add(Text(L("Card." + model.Id + ".Cons")));
            description.Children.Add(Text(L("Card." + model.Id + ".Vram")));
            var card = new Button { Content = description, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(20), Margin = new Thickness(0, 0, 18, 0), IsEnabled = !_busy && !HasPendingGeneration() };
            AutomationProperties.SetAutomationId(card, "Generation.Model." + model.Id);
            card.Click += async (_, _) =>
            {
                _modelId = model.Id; _width = model.DefaultWidth; _height = model.DefaultHeight;
                _preparationReady = false; _preparationChecked = false; _preparationCards = [];
                _page = 1; Status(""); Render(); await CheckPreparationAsync();
            };
            row.Children.Add(card);
            var examples = new Grid(); Grid.SetColumn(examples, 1); row.Children.Add(examples);
            examples.ColumnDefinitions.Add(new ColumnDefinition()); examples.ColumnDefinitions.Add(new ColumnDefinition());
            var selected = ImageGenerationExamples.All.Where(e => e.ModelId == model.Id).ToArray();
            for (var i = 0; i < selected.Length; i++)
            {
                var example = selected[i];
                var content = new StackPanel();
                content.Children.Add(ExamplePreview(example));
                content.Children.Add(Text(L("Examples." + example.TitleKey)));
                var button = new Button { Content = content, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Top, Padding = new Thickness(10), Margin = new Thickness(0, 0, 10, 0), ToolTip = L("Examples.Open") };
                AutomationProperties.SetAutomationId(button, "Generation.Example." + example.Id);
                button.Click += (_, _) =>
                {
                    try
                    {
                        var viewer = new ImageGenerationExampleWindow(example, _l);
                        if (Window.GetWindow(this) is { } owner) viewer.Owner = owner;
                        viewer.Show();
                    }
                    catch (Exception ex) { Error(ex); }
                };
                Grid.SetColumn(button, i); examples.Children.Add(button);
            }
            panel.Children.Add(row);
        }
    }
    internal static Viewbox ExamplePreview(GenerationExample example)
    {
        const int side = 512;
        var source = ImageGenerationExamples.Image(example, side);
        var canvas = new Grid { Width = side, Height = side, ClipToBounds = true };
        canvas.Children.Add(new Image { Source = source, Stretch = Stretch.Fill });
        foreach (var region in example.BlurRegions)
        {
            var overlay = new Image { Source = source, Stretch = Stretch.Fill,
                Effect = new BlurEffect { Radius = 16, KernelType = KernelType.Gaussian },
                Clip = new RectangleGeometry(new Rect(region.X * side, region.Y * side, region.Width * side, region.Height * side)) };
            canvas.Children.Add(overlay);
        }
        return new Viewbox { Child = canvas, Stretch = Stretch.Uniform, MaxHeight = 245 };
    }
}
