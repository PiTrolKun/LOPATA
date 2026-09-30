using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using Path = System.Windows.Shapes.Path;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace AIHub.Controls;

/// <summary>A keyboard-accessible button whose icon is the context gauge.</summary>
public sealed class LiteraryContextIndicator : Button
{
    private readonly Path _arc = new() { StrokeThickness = 3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    private readonly TextBlock _unknown = new() { Text = "?", FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private bool _warning;
    public LiteraryContextIndicator()
    {
        Width = Height = 38; MinWidth = MinHeight = 0; Padding = new Thickness(2); Margin = new Thickness(3, 0, 5, 5);
        Cursor = System.Windows.Input.Cursors.Hand; Background = Brushes.Transparent; BorderThickness = new Thickness(0);
        var content = new Grid { Width = 30, Height = 30 };
        var track = new Ellipse { StrokeThickness = 3, Stroke = new SolidColorBrush(Color.FromRgb(61, 113, 85)), Opacity = .45 };
        content.Children.Add(track); _arc.Stroke = new SolidColorBrush(Color.FromRgb(35, 213, 122));
        content.Children.Add(_arc); _unknown.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush"); content.Children.Add(_unknown);
        Content = content;
        var template = new ControlTemplate(typeof(Button));
        var frame = new FrameworkElementFactory(typeof(Border)); frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(20));
        frame.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        frame.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty));
        frame.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(BorderThicknessProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center); presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        frame.AppendChild(presenter); template.VisualTree = frame;
        var focus = new Trigger { Property = IsKeyboardFocusWithinProperty, Value = true };
        focus.Setters.Add(new Setter(BorderThicknessProperty, new Thickness(1)));
        focus.Setters.Add(new Setter(BorderBrushProperty, Brushes.MediumSeaGreen)); template.Triggers.Add(focus);
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(BackgroundProperty, new SolidColorBrush(Color.FromArgb(28, 35, 213, 122)))); template.Triggers.Add(hover);
        Template = template;
        System.Windows.Automation.AutomationProperties.SetAutomationId(this, "Studio.ContextIndicator");
        Loaded += (_, _) => { SystemParameters.StaticPropertyChanged += AnimationSettingsChanged; Animate(); };
        Unloaded += (_, _) => { SystemParameters.StaticPropertyChanged -= AnimationSettingsChanged; _arc.BeginAnimation(OpacityProperty, null); };
    }
    private void AnimationSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) Dispatcher.BeginInvoke(Animate); }
    public void Update(StudioContextMeter? meter, string hint, string name)
    {
        ToolTip = hint; System.Windows.Automation.AutomationProperties.SetName(this, name + ". " + hint);
        _unknown.Visibility = meter is null ? Visibility.Visible : Visibility.Collapsed;
        _arc.Data = meter is null || meter.UsedRatio <= 0 ? Geometry.Empty : DrawArc(meter.UsedRatio);
        var warning = meter?.UsedRatio >= .9;
        if (_warning != warning) { _warning = warning; Animate(); }
    }
    private void Animate()
    {
        _arc.BeginAnimation(OpacityProperty, null);
        if (_warning && SystemParameters.ClientAreaAnimation && IsLoaded)
            _arc.BeginAnimation(OpacityProperty, new DoubleAnimation(1, .3, TimeSpan.FromMilliseconds(650))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        _arc.StrokeThickness = _warning ? 4 : 3;
    }
    private static Geometry DrawArc(double value)
    {
        var ratio = Math.Clamp(value, 0, .999999); var angle = ratio * 2 * Math.PI;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(15, 2), false, false);
            context.ArcTo(new Point(15 + 13 * Math.Sin(angle), 15 - 13 * Math.Cos(angle)), new Size(13, 13), 0,
                ratio > .5, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze(); return geometry;
    }
}
