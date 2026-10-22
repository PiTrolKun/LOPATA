using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Documents;
using Button=System.Windows.Controls.Button;
using Brushes=System.Windows.Media.Brushes;
using ContextMenu=System.Windows.Controls.ContextMenu;
using MenuItem=System.Windows.Controls.MenuItem;
using Orientation=System.Windows.Controls.Orientation;

namespace AIHub.Controls;

/// <summary>One compact control can be reparented between the footer and a floating window.</summary>
public sealed class DictationWidget : Border
{
    private readonly Button _dock=Make("●","Dictation.Detach"),_mode=Make("","Dictation.Mode"),_record=Make("","Dictation.Record"),_target=Make("⌖","Dictation.Target"),_fold=Make("−","Dictation.Collapse");
    private readonly Ellipse _lamp=new(){Width=7,Height=7,Margin=new(4,0,7,0)};
    private readonly TextBlock _status=new(){FontSize=11,VerticalAlignment=VerticalAlignment.Center,MaxWidth=100,TextTrimming=TextTrimming.CharacterEllipsis};
    private readonly Grid _level=new(){Height=3,Width=36,Margin=new(6,2,6,2),VerticalAlignment=VerticalAlignment.Center};
    private readonly Border _levelFill=new(){Height=3,HorizontalAlignment=System.Windows.HorizontalAlignment.Left};
    private readonly Grid _grid=new();
    private readonly TextBlock _heading=new(){FontSize=13,FontWeight=FontWeights.SemiBold,Margin=new(4,0,6,12),Visibility=Visibility.Collapsed};
    private readonly StackPanel _left=new(){Orientation=Orientation.Horizontal},_middle=new(){Orientation=Orientation.Horizontal},_right=new(){Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
    private Func<string,string> _text=k=>k;
    private bool _hold,_recording,_busy,_pressed;
    public event Action? DetachRequested,TargetRequested,CollapseRequested;
    public event Action? ToggleRequested,PressRequested,ReleaseRequested;
    public event Action<bool>? ModeChanged;
    public bool Floating { get; private set; }
    public DictationWidget()
    {
        Padding=new(3);BorderThickness=new(1);CornerRadius=new(5);
        HorizontalAlignment=System.Windows.HorizontalAlignment.Left;VerticalAlignment=System.Windows.VerticalAlignment.Center;
        _record.Height=28;_record.FontSize=18;_mode.Height=16;_mode.FontSize=9;
        SetResourceReference(BackgroundProperty,"PanelBrush");SetResourceReference(BorderBrushProperty,"LineBrush");
        _level.SetResourceReference(System.Windows.Controls.Panel.BackgroundProperty,"LineBrush");_levelFill.SetResourceReference(BackgroundProperty,"AccentBrush");_level.Children.Add(_levelFill);
        for(var i=0;i<4;i++)_grid.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        _left.Children.Add(_dock);_left.Children.Add(_fold);
        _grid.Children.Add(_left);
        _middle.Children.Add(_mode);_middle.Children.Add(_record);Grid.SetColumn(_middle,1);_grid.Children.Add(_middle);
        var state=new StackPanel{Orientation=Orientation.Horizontal,Margin=new(4,0,2,0)};state.Children.Add(_lamp);state.Children.Add(_status);
        _right.Children.Add(state);_right.Children.Add(_level);_right.Children.Add(_target);
        Grid.SetColumn(_right,2);_grid.Children.Add(_right);
        var content=new StackPanel();content.Children.Add(_heading);content.Children.Add(_grid);Child=content;
        AutomationProperties.SetAutomationId(this,"Dictation.Widget");
        _dock.Click+=(_,_)=>DetachRequested?.Invoke();_fold.Click+=(_,_)=>CollapseRequested?.Invoke();_target.Click+=(_,_)=>TargetRequested?.Invoke();
        _mode.Click+=(_,_)=>ShowModes();
        _record.Click+=(_,_)=>{if(!_hold)ToggleRequested?.Invoke();};
        _record.PreviewMouseLeftButtonDown+=(_,e)=>{if(!_hold||_busy)return;_pressed=true;_record.CaptureMouse();PressRequested?.Invoke();e.Handled=true;};
        _record.PreviewMouseLeftButtonUp+=(_,e)=>{if(!_pressed)return;Release();e.Handled=true;};
        _record.LostMouseCapture+=(_,_)=>Release();
        _record.PreviewKeyDown+=(_,e)=>{if(_hold&&e.Key is Key.Space or Key.Enter&&!e.IsRepeat&&!_pressed){_pressed=true;PressRequested?.Invoke();e.Handled=true;}};
        _record.PreviewKeyUp+=(_,e)=>{if(_pressed&&e.Key is Key.Space or Key.Enter){Release();e.Handled=true;}};
    }
    private void Release(){if(!_pressed)return;_pressed=false;if(_record.IsMouseCaptured)_record.ReleaseMouseCapture();ReleaseRequested?.Invoke();}
    public void SetFloating(bool floating)
    {
        Floating=floating;CornerRadius=new(floating?18:5);Padding=new(floating?12:3);
        _left.Orientation=_middle.Orientation=_right.Orientation=floating?Orientation.Vertical:Orientation.Horizontal;
        _heading.Visibility=floating?Visibility.Visible:Visibility.Collapsed;
        _record.SetResourceReference(StyleProperty,floating?"PrimaryButtonStyle":"SecondaryButtonStyle");
        _record.Margin=floating?new(4,4,8,4):new(1);
        _record.FontSize=floating?26:18;_record.MinWidth=floating?76:36;_record.Height=floating?48:28;
        _mode.Height=floating?20:16;_mode.FontSize=floating?11:9;
        _level.Width=floating?70:36;
        _dock.ToolTip=_text(floating?"Dictation.Dock":"Dictation.Detach");
    }
    public void Refresh(Func<string,string> text,bool hold,bool recording,bool processing,bool busy,string status,double level,bool starting=false)
    {
        _text=text;_hold=hold;_recording=recording;_busy=busy;
        _heading.Text=text("Dictation.Title");
        _mode.Content=text(hold?"Dictation.Hold":"Dictation.Toggle")+" ▾";
        _record.Content=recording||starting?"■":"🎤";
        _record.ToolTip=text(recording||starting?"Dictation.Stop":"Dictation.Start");
        AutomationProperties.SetName(_record,(string)_record.ToolTip);
        _mode.IsEnabled=!recording&&!processing&&!busy;
        _record.IsEnabled=recording||starting||_pressed||(!busy&&!processing);_target.IsEnabled=!busy&&!recording&&!processing;
        _target.ToolTip=text("Dictation.Target");_fold.ToolTip=text("Dictation.Collapse");_dock.ToolTip=text(Floating?"Dictation.Dock":"Dictation.Detach");
        foreach(var button in new[]{_target,_fold,_dock})AutomationProperties.SetName(button,button.ToolTip?.ToString()??"");
        _lamp.Fill=recording?Brushes.OrangeRed:status=="Dictation.Error"||status=="Dictation.TargetLost"||status=="Dictation.HotkeyConflict"?Brushes.Crimson:processing||busy?Brushes.Goldenrod:Brushes.SeaGreen;
        _status.Text=text(status);_status.ToolTip=text(status);_levelFill.Width=Math.Clamp(level/.15,0,1)*_level.Width;
        SetResourceReference(TextElement.ForegroundProperty,"TextPrimaryBrush");
    }
    private void ShowModes()
    {
        var menu=new ContextMenu();
        foreach(var hold in new[]{false,true}) {
            var item=new MenuItem{Header=_text(hold?"Dictation.Hold":"Dictation.Toggle"),IsCheckable=true,IsChecked=_hold==hold};
            item.Click+=(_,_)=>ModeChanged?.Invoke(hold);menu.Items.Add(item);
        }
        menu.PlacementTarget=_mode;menu.IsOpen=true;
    }
    private static Button Make(string content,string id)
    {
        var button=new Button{Content=content,Padding=new(4,1,4,1),Margin=new(1),MinWidth=22,MinHeight=18,Height=20,FontSize=11,Focusable=false,VerticalAlignment=VerticalAlignment.Center};
        AutomationProperties.SetAutomationId(button,id);button.SetResourceReference(StyleProperty,"SecondaryButtonStyle");return button;
    }
}

public sealed class DictationFloatingWindow : Window
{
    public DictationFloatingWindow(Window owner,DictationWidget widget,Func<string,string> text)
    {
        Title=text("Dictation.Title");ShowInTaskbar=false;ShowActivated=false;Topmost=true;
        WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;SizeToContent=SizeToContent.WidthAndHeight;
        AllowsTransparency=true;Background=Brushes.Transparent;
        SourceInitialized+=(_,_)=>System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle)?.AddHook(NoActivate);
        Resources.MergedDictionaries.Add(owner.Resources);Content=widget;
        AutomationProperties.SetAutomationId(this,"Dictation.FloatingWindow");
        PreviewMouseLeftButtonDown+=(_,e)=>{
            if(e.OriginalSource is DependencyObject d) {
                for(var p=d;p is not null;p=p is Visual?VisualTreeHelper.GetParent(p):LogicalTreeHelper.GetParent(p))if(p is Button)return;
            }
            if(e.ClickCount==1)try{DragMove();}catch(InvalidOperationException){};
        };
        var area=SystemParameters.WorkArea;Left=Math.Max(area.Left,area.Right-300);Top=Math.Max(area.Top,area.Bottom-160);
    }
    private static nint NoActivate(nint hwnd,int message,nint w,nint l,ref bool handled){if(message==0x21){handled=true;return 3;}return 0;}
}
