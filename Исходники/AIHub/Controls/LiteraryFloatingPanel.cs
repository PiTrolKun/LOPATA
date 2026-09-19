using System.Windows;
using Panel = System.Windows.Controls.Panel;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Button = System.Windows.Controls.Button;

namespace AIHub.Controls;

public sealed class LiteraryFloatingPanel : Canvas
{
    private readonly Border _frame;
    private readonly UIElement _commands;
    private readonly FrameworkElement _widthReference;
    private readonly Func<double> _parkingBottomInset;
    private bool _parked = true;
    public LiteraryFloatingPanel(UIElement commands, FrameworkElement widthReference, Func<double> parkingBottomInset, Func<string,string> l)
    {
        _commands = commands; _widthReference = widthReference; _parkingBottomInset = parkingBottomInset;
        _commands.Visibility = Visibility.Collapsed;
        var root = new DockPanel();
        var header = new DockPanel();
        var collapse = new Button { Content="▾",Padding=new Thickness(6,1,6,1),MinWidth=25,MinHeight=0,Height=24,FontSize=12,ToolTip=l("Studio.Expand") };
        collapse.SetResourceReference(StyleProperty,"SecondaryButtonStyle"); DockPanel.SetDock(collapse,Dock.Right); header.Children.Add(collapse);
        var grip = new Thumb { Height=22,MinWidth=110,Cursor=System.Windows.Input.Cursors.SizeAll,ToolTip=l("Studio.Drag") };
        grip.SetResourceReference(BackgroundProperty,"LineBrush");
        var gripArea = new Grid(); gripArea.Children.Add(grip);
        var caption = new TextBlock { Text=l("Studio.ExpandCaption"),FontSize=12,Margin=new Thickness(6,0,8,0),
            HorizontalAlignment=System.Windows.HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Center,IsHitTestVisible=false };
        caption.SetResourceReference(TextBlock.ForegroundProperty,"TextPrimaryBrush");
        gripArea.Children.Add(caption); header.Children.Add(gripArea);
        System.Windows.Automation.AutomationProperties.SetName(collapse,l("Studio.Expand"));
        DockPanel.SetDock(header,Dock.Top); root.Children.Add(header); root.Children.Add(commands);
        _frame = LiteraryWorkspaceParts.Card(root); _frame.Padding = new Thickness(5);
        Children.Add(_frame); SetLeft(_frame,0); SetTop(_frame,0); Panel.SetZIndex(this,50);
        collapse.Click += (_,_) =>
        {
            _commands.Visibility = _commands.Visibility==Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            collapse.Content = _commands.Visibility==Visibility.Visible ? "▴" : "▾";
            collapse.ToolTip = l(_commands.Visibility==Visibility.Visible ? "Studio.Collapse" : "Studio.Expand");
            caption.Text = l(_commands.Visibility==Visibility.Visible ? "Studio.CollapseCaption" : "Studio.ExpandCaption");
            System.Windows.Automation.AutomationProperties.SetName(collapse,(string)collapse.ToolTip);
            UpdateWidth();
        };
        grip.DragDelta += (_,e) => { _parked=false; MoveTo(GetLeft(_frame)+e.HorizontalChange,GetTop(_frame)+e.VerticalChange); };
        SizeChanged += (_,_) => UpdateWidth(); _frame.SizeChanged += (_,_) => Position();
        LayoutUpdated += (_,_) => { if (_parked && IsVisible) Position(); };
        Loaded += (_,_) => { _widthReference.SizeChanged += ReferenceSizeChanged; UpdateWidth(); };
        Unloaded += (_,_) => _widthReference.SizeChanged -= ReferenceSizeChanged;
    }
    private void ReferenceSizeChanged(object sender, SizeChangedEventArgs e) => UpdateWidth();
    private void UpdateWidth()
    {
        _frame.MaxWidth = Math.Max(0,ActualWidth);
        _frame.Width = _commands.Visibility == Visibility.Visible ? Math.Min(_widthReference.ActualWidth,ActualWidth) : double.NaN;
        Position();
    }
    private void Position()
    {
        var left = GetLeft(_frame); var top = GetTop(_frame);
        if (_parked && IsLoaded && _widthReference.IsLoaded
            && PresentationSource.FromVisual(this)==PresentationSource.FromVisual(_widthReference))
        {
            var corner = _widthReference.TranslatePoint(new System.Windows.Point(_widthReference.ActualWidth-24,
                _widthReference.ActualHeight-_parkingBottomInset()),this);
            left = corner.X-_frame.ActualWidth; top = corner.Y-_frame.ActualHeight;
        }
        MoveTo(left,top);
    }
    private void MoveTo(double left, double top)
    {
        left = Math.Clamp(left,0,Math.Max(0,ActualWidth-_frame.ActualWidth));
        top = Math.Clamp(top,0,Math.Max(0,ActualHeight-_frame.ActualHeight));
        // LayoutUpdated must be idempotent: intermediate out-of-bounds positions and
        // subpixel noise would queue another arrange pass even when the panel stays put.
        if (Math.Abs(GetLeft(_frame)-left)>.01) SetLeft(_frame,left);
        if (Math.Abs(GetTop(_frame)-top)>.01) SetTop(_frame,top);
    }
}
