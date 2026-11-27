using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Xml.Linq;
using AIHub.Services.VisualConstructor;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using ComboBox = System.Windows.Controls.ComboBox;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;
using SystemColors = System.Windows.SystemColors;
using SystemFonts = System.Windows.SystemFonts;

namespace AIHub.Controls.VisualConstructor;

internal sealed partial class ConstructorEditor
{
    private readonly Border _sampleCaption = new();
    private readonly Border _sampleBody = new() { Background = System.Windows.Media.Brushes.Transparent };
    private readonly AdornerDecorator _sampleDecoration = new() { ClipToBounds = true };
    private readonly ComboBox _previewScale = NewChoiceBox();

    private DockPanel BuildPreview()
    {
        AutomationProperties.SetAutomationId(_sample, "VisualConstructor.FormPreview");
        _sample.SetResourceReference(Border.BorderBrushProperty, SystemColors.ActiveBorderBrushKey);
        _sample.SetResourceReference(Border.BackgroundProperty, SystemColors.WindowBrushKey);
        _sample.SetResourceReference(TextElement.ForegroundProperty, SystemColors.WindowTextBrushKey);
        _sampleCaption.SetResourceReference(Border.BackgroundProperty, SystemColors.ActiveCaptionBrushKey);
        _sampleTitle.SetResourceReference(TextBlock.ForegroundProperty, SystemColors.ActiveCaptionTextBrushKey);
        _sampleTitle.SetResourceReference(TextBlock.FontFamilyProperty, SystemFonts.CaptionFontFamilyKey);
        _sampleTitle.SetResourceReference(TextBlock.FontSizeProperty, SystemFonts.CaptionFontSizeKey);
        _sampleCaption.Child = _sampleTitle;
        DockPanel.SetDock(_sampleCaption, Dock.Top);
        _sampleBody.PreviewMouseDown += SelectFromPreview;
        _sampleDecoration.Child = _sampleBody;
        var sampleLayers = new Grid { Children = { new DockPanel { Children = { _sampleCaption, _sampleDecoration } } } };
        _sample.Child = sampleLayers;
        // The sample represents the exported application and uses the standard window controls.
        _sample.Resources[typeof(ScrollBar)] = new Style(typeof(ScrollBar));
        var fit = new Viewbox { StretchDirection = StretchDirection.DownOnly, Child = _sample };
        var viewport = new ScrollViewer { Content = fit, CanContentScroll = false, Focusable = true };
        AutomationProperties.SetAutomationId(viewport, "VisualConstructor.PreviewViewport");
        var scaleBar = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        var heading = Label("Preview", new Thickness(0, 0, 6, 0));
        heading.VerticalAlignment = VerticalAlignment.Center;
        heading.FontWeight = FontWeights.SemiBold;
        _previewScale.Width = 86;
        AutomationProperties.SetAutomationId(_previewScale, "VisualConstructor.PreviewScale");
        scaleBar.Children.Add(heading);
        scaleBar.Children.Add(_previewScale);
        AddPreviewSizeTools(scaleBar, sampleLayers, viewport);
        scaleBar.SizeChanged += (_, _) => heading.Visibility = scaleBar.ActualWidth >= 440 ? Visibility.Visible : Visibility.Collapsed;
        _previewScale.SelectionChanged += (_, _) =>
        {
            var actual = _previewScale.SelectedValue as string == "actual";
            fit.Stretch = actual ? Stretch.None : Stretch.Uniform;
            viewport.HorizontalScrollBarVisibility = viewport.VerticalScrollBarVisibility =
                actual ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            if (!actual) viewport.ScrollToHome();
        };
        ConfigurePreviewLabels();
        DockPanel.SetDock(scaleBar, Dock.Top);
        return new DockPanel { Children = { scaleBar, viewport } };
    }

    private void ConfigurePreviewLabels()
    {
        var current = _previewScale.SelectedValue ?? "fit";
        _previewScale.ItemsSource = new[] { new Choice("fit", T("PreviewFit")), new Choice("actual", "1:1") };
        _previewScale.SelectedValue = current;
        _previewScale.ToolTip = T("PreviewScale");
        _sample.ToolTip = T("PreviewSelectHint");
        AutomationProperties.SetName(_previewScale, T("PreviewScale"));
        ConfigurePreviewSizeLabels();
    }

    private void UpdateCanvas()
    {
        _sampleTitle.Text = _title.Text;
        var size = PreviewDimensions();
        _sample.Width = size.Width;
        _sample.Height = size.Height;
        _previewSizeReadout.Text = $"{size.Width:0.#} × {size.Height:0.#}";
        _applyPreviewSize.IsEnabled = _project is not null && _previewSizeMode.SelectedValue as string != "opening";
    }

    private void ApplyPreviewTheme(XElement form)
    {
        _sampleCaption.Visibility = (string?)form.Attribute("WindowStyle") == "None" ? Visibility.Collapsed : Visibility.Visible;
        ApplyBrush("Background", Border.BackgroundProperty, SystemColors.WindowBrushKey);
        ApplyBrush("Foreground", TextElement.ForegroundProperty, SystemColors.WindowTextBrushKey);

        void ApplyBrush(string name, DependencyProperty property, ResourceKey systemBrush)
        {
            // Project markup is inspected as data. Only literal, solid WPF colours are used here.
            var ns = ConstructorProject.Wpf;
            var literal = ((string?)form.Attribute(name)
                ?? (string?)form.Elements().FirstOrDefault(node => node.Name.LocalName == form.Name.LocalName + "." + name)
                    ?.Element(ns + "SolidColorBrush")?.Attribute("Color"))?.Trim();
            if (literal is { Length: > 0 } && (literal.StartsWith('#') || literal.All(char.IsLetter)))
            {
                try
                {
                    if (ColorConverter.ConvertFromString(literal) is Color color)
                    {
                        var brush = new SolidColorBrush(color);
                        brush.Freeze();
                        _sample.SetValue(property, brush);
                        return;
                    }
                }
                catch (FormatException) { }
            }
            _sample.SetResourceReference(property, systemBrush);
        }
    }
}
