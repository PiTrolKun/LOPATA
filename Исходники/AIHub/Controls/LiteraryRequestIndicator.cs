using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using UserControl = System.Windows.Controls.UserControl;
using FontFamily = System.Windows.Media.FontFamily;
using Point = System.Windows.Point;

namespace AIHub.Controls;

/// <summary>Reserved space and a render-only rotation keep request feedback out of layout calculations.</summary>
public sealed class LiteraryRequestIndicator : UserControl
{
    private readonly TextBlock _label = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly RotateTransform _rotation = new();
    public bool IsActive { get; private set; }
    public string StatusText => _label.Text;

    public LiteraryRequestIndicator()
    {
        Height = 28; Width = 250; IsHitTestVisible = false; Visibility = Visibility.Hidden;
        var row = new Grid(); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = new GridLength(38) });
        _label.SetResourceReference(TextBlock.ForegroundProperty,"TextSecondaryBrush");
        _label.SetResourceReference(TextBlock.FontSizeProperty,"UiSmallFontSize");
        _label.TextAlignment = TextAlignment.Right; row.Children.Add(_label);
        var gear = new TextBlock { Text = "⚙", FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = 20,
            Width = 28, Height = 28, TextAlignment = TextAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = _rotation, RenderTransformOrigin = new Point(.5,.5) };
        gear.SetResourceReference(TextBlock.ForegroundProperty,"TextPrimaryBrush");
        Grid.SetColumn(gear,1); row.Children.Add(gear); Content = row;
        Loaded += (_,_) => { if (IsActive) Animate(); };
        Unloaded += (_,_) => _rotation.BeginAnimation(RotateTransform.AngleProperty,null);
    }
    public void ShowActivity(string text)
    {
        _label.Text = text; System.Windows.Automation.AutomationProperties.SetName(this,text);
        if (IsActive) return;
        IsActive = true; Visibility = Visibility.Visible;
        if (IsLoaded) Animate();
    }
    public void Stop()
    {
        IsActive = false; _rotation.BeginAnimation(RotateTransform.AngleProperty,null); _rotation.Angle = 0;
        Visibility = Visibility.Hidden; _label.Text = "";
        System.Windows.Automation.AutomationProperties.SetName(this,"");
    }
    private void Animate()
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        _rotation.BeginAnimation(RotateTransform.AngleProperty,new DoubleAnimation(0,360,TimeSpan.FromSeconds(1.5))
            { RepeatBehavior = RepeatBehavior.Forever });
    }
}
