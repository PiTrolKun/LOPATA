using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AIHub.Controls;

public partial class StartupBusyControl : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(StartupBusyControl), new PropertyMetadata(string.Empty));

    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public StartupBusyControl()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateAnimation();
        IsVisibleChanged += (_, _) => UpdateAnimation();
        Unloaded += (_, _) => SpinnerRotation.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    private void UpdateAnimation()
    {
        SpinnerRotation.BeginAnimation(RotateTransform.AngleProperty, IsVisible && IsLoaded
            ? new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever }
            : null);
    }
}
