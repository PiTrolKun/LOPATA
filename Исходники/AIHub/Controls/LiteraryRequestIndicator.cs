using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using UserControl = System.Windows.Controls.UserControl;
using FontFamily = System.Windows.Media.FontFamily;
using Point = System.Windows.Point;

namespace AIHub.Controls;

/// <summary>A fixed-height status line. Overflow travels in both directions with reading pauses.</summary>
public sealed class LiteraryRequestIndicator : UserControl
{
    private readonly TextBlock _label = new() { TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _viewport = new() { ClipToBounds = true };
    private readonly TextBlock _gear = new() { Text = "ⓘ", FontFamily = new FontFamily("Segoe UI Symbol"),
        FontSize = 20, Width = 28, Height = 28, TextAlignment = TextAlignment.Center,
        HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly RotateTransform _rotation = new();
    private readonly TranslateTransform _travel = new();
    public bool IsActive { get; private set; }
    public string StatusText => _label.Text;
    public string DetailsHint { get; set; } = "";

    public LiteraryRequestIndicator()
    {
        Height = 28; HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        var row = new Grid(); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = new GridLength(34) });
        _label.SetResourceReference(TextBlock.ForegroundProperty,"TextSecondaryBrush");
        _label.SetResourceReference(TextBlock.FontSizeProperty,"UiSmallFontSize");
        _label.RenderTransform = _travel;
        _viewport.Child = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Children = { _label } };
        row.Children.Add(_viewport);
        _gear.RenderTransform = _rotation; _gear.RenderTransformOrigin = new Point(.5,.5);
        _gear.SetResourceReference(TextBlock.ForegroundProperty,"TextPrimaryBrush");
        Grid.SetColumn(_gear,1); row.Children.Add(_gear); Content = row;
        _viewport.SizeChanged += (_,_) => QueueMotion();
        Loaded += (_,_) => { QueueMotion(); if (IsActive) Animate(); };
        Unloaded += (_,_) => { _rotation.BeginAnimation(RotateTransform.AngleProperty,null); _travel.BeginAnimation(TranslateTransform.XProperty,null); };
    }

    public void SetStatus(string text)
    {
        var line = text.Replace("\r\n", " · ").Replace('\n', ' ').Replace('\r', ' ');
        if (_label.Text == line) return;
        _label.Text = line;
        ToolTip = DetailsHint.Length == 0 ? text : text + "\n" + DetailsHint;
        System.Windows.Automation.AutomationProperties.SetName(this,line);
        QueueMotion();
    }

    public void ShowActivity(string text)
    {
        SetStatus(text);
        if (IsActive) return;
        IsActive = true; _gear.Text = "⚙";
        if (IsLoaded) Animate();
    }

    public void Stop()
    {
        IsActive = false; _rotation.BeginAnimation(RotateTransform.AngleProperty,null);
        _rotation.Angle = 0; _gear.Text = "ⓘ";
        // The result or error remains readable after the animation stops.
    }

    private void QueueMotion() => Dispatcher.BeginInvoke(RecalculateMotion, DispatcherPriority.Loaded);

    private void RecalculateMotion()
    {
        _travel.BeginAnimation(TranslateTransform.XProperty,null); _travel.X = 0;
        _label.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var overflow = _label.DesiredSize.Width - _viewport.ActualWidth;
        if (overflow <= 1 || !SystemParameters.ClientAreaAnimation) return;
        var move = TimeSpan.FromSeconds(Math.Max(2, overflow / 42));
        var pause = TimeSpan.FromSeconds(1.3);
        var animation = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(pause)));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(-overflow, KeyTime.FromTimeSpan(pause + move)));
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(-overflow, KeyTime.FromTimeSpan(pause + move + pause)));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(pause + move + pause + move)));
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(pause + move + pause + move + pause)));
        _travel.BeginAnimation(TranslateTransform.XProperty, animation);
    }

    private void Animate()
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        _rotation.BeginAnimation(RotateTransform.AngleProperty,new DoubleAnimation(0,360,TimeSpan.FromSeconds(1.5))
            { RepeatBehavior = RepeatBehavior.Forever });
    }
}
