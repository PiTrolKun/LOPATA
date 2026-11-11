using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using Image = System.Windows.Controls.Image;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace AIHub.Controls;

public sealed partial class ImageGenerationControl
{
    private bool _removingModel;
    internal Func<ImageGenerationModel, bool>? CanRemoveModel { get; set; }
    internal Func<ImageGenerationModel, Task>? RemoveModelRequested { get; set; }
    internal bool CanRemoveModels => _page == 0 && CanChangeModel;

    internal void SetModelRemovalBusy(bool busy)
    {
        _removingModel = busy;
        if (!busy)
        {
            _preparationReady = _preparationChecked = false;
            _preparationCards = [];
        }
        Render();
    }

    private void RenderSelection(StackPanel panel)
    {
        panel.Children.Add(Text(L("ChooseModel")));
        panel.Children.Add(Text(L("Examples.TestNote")));
        foreach (var model in ImageGenerationCatalog.Manifest.Models)
        {
            var key = model.Family ?? model.Id;
            var row = new Grid { Margin = new Thickness(0, 8, 0, 16) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var description = new StackPanel();
            var title = model.Family is null ? model.Name : L("Qwen." + key);
            description.Children.Add(Text(title, true));
            description.Children.Add(Text(L("Model." + key)));
            description.Children.Add(Text(L("Card." + key + ".Pros")));
            description.Children.Add(Text(L("Card." + key + ".Cons")));
            description.Children.Add(Text(L("Card." + key + ".Vram")));
            var choose = SelectionButton(model, description);
            AutomationProperties.SetName(choose, title);
            if (RemoveModelRequested is null)
            {
                choose.Padding = new Thickness(20);
                choose.Margin = new Thickness(0, 0, 18, 0);
                row.Children.Add(choose);
            }
            else
            {
                choose.Padding = new Thickness(0);
                choose.BorderThickness = new Thickness(0);
                var cardContent = new StackPanel();
                cardContent.Children.Add(choose);
                cardContent.Children.Add(RemovalButton(model));
                var card = new Border { Child = cardContent, Padding = new Thickness(20), BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 18, 0) };
                card.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
                card.SetResourceReference(Border.BackgroundProperty, "SecondaryButtonBackgroundBrush");
                row.Children.Add(card);
            }
            var examples = new Grid(); Grid.SetColumn(examples, 1); row.Children.Add(examples);
            examples.ColumnDefinitions.Add(new ColumnDefinition()); examples.ColumnDefinitions.Add(new ColumnDefinition());
            var selected = ImageGenerationExamples.All.Where(e => e.ModelId == model.Id).Take(2).ToArray();
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
    private Button SelectionButton(ImageGenerationModel model, object content)
    {
        var button = new Button { Content = content, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top, IsEnabled = CanChangeModel };
        AutomationProperties.SetAutomationId(button, "Generation.Model." + model.Id);
        button.Click += async (_, _) =>
        {
            if (!CanChangeModel) return;
            _modelId = model.Id;
            _width = model.DefaultWidth;
            _height = model.DefaultHeight;
            _preparationReady = _preparationChecked = false;
            _preparationCards = [];
            _page = 1;
            Status("");
            Render();
            await CheckPreparationAsync();
        };
        return button;
    }

    private Button RemovalButton(ImageGenerationModel model)
    {
        var button = MusicAudioUi.RemoveModelButton(() => { });
        button.HorizontalAlignment = HorizontalAlignment.Left;
        button.VerticalAlignment = VerticalAlignment.Center;
        button.IsEnabled = CanRemoveModels && CanRemoveModel?.Invoke(model) == true;
        button.ToolTip = button.IsEnabled ? string.Format(L("Remove.Tooltip"), model.Name) : L("Remove.Unavailable");
        AutomationProperties.SetName(button, string.Format(L("Remove.Tooltip"), model.Name));
        AutomationProperties.SetAutomationId(button, "Generation.Remove." + model.Id);
        button.Click += async (_, _) =>
        {
            if (CanRemoveModels && CanRemoveModel?.Invoke(model) == true && RemoveModelRequested is { } action)
                await action(model);
        };
        return button;
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
