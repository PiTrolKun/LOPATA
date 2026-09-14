using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Brush = System.Windows.Media.Brush;

namespace AIHub.Controls;

// Visible entry point to the same findings. Neither opening nor closing edits text.
public sealed class CalibrationFindingBubble
{
    private readonly TextBox _input;
    private readonly string _id;
    private readonly Func<string,string> _l;
    private readonly Popup _popup = new() { AllowsTransparency=true, StaysOpen=false, Placement=PlacementMode.Bottom };
    private IReadOnlyList<CalibrationFinding> _findings=[];
    private Window? _owner;
    public Grid Host { get; } = new();
    public Button Badge { get; } = new() { Content="!", Width=32, Height=32, MinWidth=0, Padding=new Thickness(0),
        FontSize=23, FontWeight=FontWeights.Bold, Margin=new Thickness(8,4,0,0), VerticalAlignment=VerticalAlignment.Top,
        Visibility=Visibility.Hidden, Tag="CalibrationFindingBadge" };
    public CalibrationFindingBubble(TextBox input,string id,Func<string,string> l)
    {
        _input=input;_id=id;_l=l;
        Host.Margin=input.Margin; input.Margin=new Thickness(0);
        Host.ColumnDefinitions.Add(new ColumnDefinition());Host.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
        Host.Children.Add(input);Grid.SetColumn(Badge,1);Host.Children.Add(Badge);
        Badge.Background=new SolidColorBrush(System.Windows.Media.Color.FromRgb(246,192,65));Badge.Foreground=System.Windows.Media.Brushes.Black;
        var border=new FrameworkElementFactory(typeof(Border));border.SetValue(Border.CornerRadiusProperty,new CornerRadius(16));
        border.SetValue(Border.BackgroundProperty,new TemplateBindingExtension(System.Windows.Controls.Control.BackgroundProperty));
        var content=new FrameworkElementFactory(typeof(ContentPresenter));content.SetValue(FrameworkElement.HorizontalAlignmentProperty,System.Windows.HorizontalAlignment.Center);content.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);
        border.AppendChild(content);Badge.Template=new ControlTemplate(typeof(Button)){VisualTree=border};
        Badge.Click+=(_,_)=> { if(_popup.IsOpen)Close();else Open(); };
        _popup.PlacementTarget=Badge;
        _popup.Closed+=(_,_)=>Detach();
        input.TextChanged+=(_,_)=>Close();input.Unloaded+=(_,_)=>Close();input.SizeChanged+=(_,_)=>Close();
    }
    public void Set(IReadOnlyList<CalibrationFinding> findings)
    {
        Close();_findings=findings.Where(f=>f.Target.FieldId==_id||f.Related.Any(s=>s.FieldId==_id)).ToArray();
        Badge.Visibility=_findings.Count==0?Visibility.Hidden:Visibility.Visible;
        Badge.ToolTip=string.Format(_l("Literary.Analysis.OpenFindings"),_findings.Count);
        System.Windows.Automation.AutomationProperties.SetName(Badge,(string)Badge.ToolTip);
    }
    private Brush Color(string key,Brush fallback)=>_input.TryFindResource(key) as Brush??fallback;
    private TextBlock Text(string text,bool secondary=false)=>new(){Text=text,TextWrapping=TextWrapping.Wrap,
        Foreground=Color(secondary?"TextSecondaryBrush":"TextPrimaryBrush",System.Windows.Media.Brushes.Black),
        FontSize=_input.FontSize,FontWeight=FontWeights.Normal,FontFamily=_input.FontFamily,Margin=new Thickness(0,0,0,8)};
    private void Open()
    {
        if(_findings.Count==0)return;
        _owner=Window.GetWindow(_input);
        var width=Math.Max(240,Math.Min(440,(_owner?.ActualWidth??600)-64));
        var body=new StackPanel { Margin=new Thickness(16) };
        var header=new DockPanel();var close=new Button{Content="×",Padding=new Thickness(0),FontWeight=FontWeights.Normal,FontSize=20,Width=30,Height=30,MinWidth=0,ToolTip=_l("Literary.Calibration.Close")};
        if(_input.TryFindResource("SecondaryButtonStyle") is Style closeStyle)close.Style=closeStyle;
        close.Click+=(_,_)=>Close();DockPanel.SetDock(close,Dock.Right);header.Children.Add(close);
        var title=Text(string.Format(_l("Literary.Analysis.FindingsTitle"),_findings.Count));title.FontWeight=FontWeights.Bold;header.Children.Add(title);body.Children.Add(header);
        foreach(var finding in _findings)
        {
            var span=finding.Target.FieldId==_id?finding.Target:finding.Related.First(s=>s.FieldId==_id);
            if(span.Start<0||span.Start+span.Length>_input.Text.Length)continue;
            var quote=_input.Text.Substring(span.Start,span.Length);
            body.Children.Add(new Border { Height=1,Background=Color("LineBrush",System.Windows.Media.Brushes.Gray),Margin=new Thickness(0,8,0,10) });
            body.Children.Add(Text("«"+(quote.Length>180?quote[..180]+"…":quote)+"»",true));
            body.Children.Add(Text(finding.Explanation));
            if(finding.Target.FieldId!=_id||finding.Suggestions.Count==0)continue;
            body.Children.Add(Text(_l("Literary.Analysis.ReplaceWord"),true));
            var variants=new WrapPanel();body.Children.Add(variants);
            foreach(var replacement in finding.Suggestions)
            {
                var button=new Button{Content=replacement,Padding=new Thickness(10,6,10,6),Margin=new Thickness(0,0,6,6),Tag="CalibrationBubbleReplacement"};
                if(_input.TryFindResource("SecondaryButtonStyle") is Style variantStyle)button.Style=variantStyle;
                button.Click+=(_,_)=> { Close(); if(span.Start+span.Length>_input.Text.Length||_input.Text.Substring(span.Start,span.Length)!=quote)return;
                    _input.Focus();_input.Select(span.Start,span.Length);_input.SelectedText=replacement; };
                variants.Children.Add(button);
            }
        }
        var scroll=new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,
            MaxHeight=Math.Max(180,Math.Min(440,(_owner?.ActualHeight??700)-100))};
        var bubble=new Border{Width=width,CornerRadius=new CornerRadius(18),BorderThickness=new Thickness(1),
            BorderBrush=Color("LineBrush",System.Windows.Media.Brushes.Gray),Background=Color("PanelBrush",System.Windows.Media.Brushes.White),Child=scroll,Margin=new Thickness(0,7,0,0)};
        var cloud=new Grid {Width=width};cloud.Children.Add(bubble);
        cloud.Children.Add(new System.Windows.Shapes.Path {Data=Geometry.Parse("M 0,8 L 8,0 L 16,8"),
            Fill=bubble.Background,Stroke=bubble.BorderBrush,StrokeThickness=1,Width=16,Height=8,Margin=new Thickness(0,0,8,0),
            HorizontalAlignment=System.Windows.HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Top,IsHitTestVisible=false});
        bubble.PreviewKeyDown+=(_,e)=> {if(e.Key==Key.Escape){Close();Badge.Focus();e.Handled=true;}};
        _popup.Child=cloud;_popup.HorizontalOffset=Badge.ActualWidth-width;
        _popup.PopupAnimation=SystemParameters.ClientAreaAnimation?PopupAnimation.Fade:PopupAnimation.None;
        if(_owner is not null){_owner.Deactivated+=Dismiss;_owner.LocationChanged+=Dismiss;_owner.SizeChanged+=OwnerResized;_owner.AddHandler(ScrollViewer.ScrollChangedEvent,new ScrollChangedEventHandler(Scrolled));}
        _popup.IsOpen=true;
        if(SystemParameters.ClientAreaAnimation){var move=new TranslateTransform();cloud.RenderTransform=move;move.BeginAnimation(TranslateTransform.YProperty,new DoubleAnimation(5,0,TimeSpan.FromMilliseconds(160)){EasingFunction=new QuadraticEase{EasingMode=EasingMode.EaseOut}});}
        close.Focus();
    }
    private void Close()=>_popup.IsOpen=false;
    private void Dismiss(object? sender,EventArgs e)=>Close();
    private void OwnerResized(object sender,SizeChangedEventArgs e)=>Close();
    private void Scrolled(object sender,ScrollChangedEventArgs e){if(e.VerticalChange!=0||e.HorizontalChange!=0)Close();}
    private void Detach()
    {
        if(_owner is null)return;
        _owner.Deactivated-=Dismiss;_owner.LocationChanged-=Dismiss;_owner.SizeChanged-=OwnerResized;
        _owner.RemoveHandler(ScrollViewer.ScrollChangedEvent,new ScrollChangedEventHandler(Scrolled));_owner=null;
    }
}
