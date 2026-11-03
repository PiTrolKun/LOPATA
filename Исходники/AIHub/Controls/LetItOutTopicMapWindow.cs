using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AIHub.Models;
using Button=System.Windows.Controls.Button;
using TextBox=System.Windows.Controls.TextBox;
using Point=System.Windows.Point;
using Brushes=System.Windows.Media.Brushes;
using Control=System.Windows.Controls.Control;

namespace AIHub.Controls;

internal sealed class LetItOutTopicMapWindow:Window
{
    private LetItOutTreeDocument _map;
    private IReadOnlyList<LetItOutMemoryPair> _pairs;
    private readonly Func<string,string> _l;
    private readonly Dictionary<string,LetItOutTreeNode> _nodes;
    private Dictionary<string,Point> _positions=[];
    private readonly Canvas _canvas=new(){Background=Brushes.Transparent};
    private readonly ScrollViewer _scroll=new(){HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    private readonly StackPanel _detail=new(){Margin=new(14)};
    private readonly TextBlock _status=new(){Margin=new(12)};
    private readonly Slider _zoom=new(){Minimum=.5,Maximum=2.5,Value=.7,Width=130,Margin=new(8)};
    private readonly TextBox _search=new(){Width=190,Margin=new(6),Padding=new(6)};
    private string _scope="",_selected="";private Point? _drag;private double _x,_y;
    internal int Revision=>_map.Revision;
    internal void Update(LetItOutTreeDocument document,IReadOnlyList<LetItOutMemoryPair> pairs)
    {
        _map=document;_pairs=pairs;_nodes.Clear();foreach(var node in document.Nodes)_nodes[node.Id]=node;
        if(_nodes.ContainsKey(_selected))Select(_selected,false);else Render();
    }
    internal void FocusNode(string? id){if(id is not null&&_nodes.ContainsKey(id))Select(id,true);Activate();}
    internal LetItOutTopicMapWindow(LetItOutTreeDocument map,IReadOnlyList<LetItOutMemoryPair> pairs,Func<string,string> l,string? focus,Window? owner=null)
    {
        Owner=owner;if(owner is not null)Resources.MergedDictionaries.Add(owner.Resources);
        _map=map;_pairs=pairs;_l=l;_nodes=map.Nodes.ToDictionary(n=>n.Id);
        Title=l("LetItOut.Tree.Title")+" · "+map.Title;Width=1400;Height=900;MinWidth=900;MinHeight=550;
        SetResourceReference(BackgroundProperty,"HeaderBackgroundBrush");SetResourceReference(ForegroundProperty,"TextPrimaryBrush");
        AutomationProperties.SetAutomationId(this,"LetItOut.Map.Window");
        var root=new DockPanel();Content=root;var top=new StackPanel{Margin=new(12)};DockPanel.SetDock(top,Dock.Top);root.Children.Add(top);
        top.Children.Add(Label(map.Title,23));top.Children.Add(Label(l("LetItOut.Tree.Disclaimer"),12));
        var tools=new WrapPanel();top.Children.Add(tools);
        tools.Children.Add(Action("LetItOut.Tree.Overview",()=>{_scope="";_zoom.Value=.7;Render();},"LetItOut.Map.Overview"));
        tools.Children.Add(Action("LetItOut.Tree.Parent",()=>{if(_nodes.TryGetValue(_scope,out var n))_scope=n.Parent;Render();},"LetItOut.Map.Parent"));
        tools.Children.Add(_zoom);tools.Children.Add(_search);
        tools.Children.Add(Action("LetItOut.Tree.Search",Find,"LetItOut.Map.Find"));
        AutomationProperties.SetAutomationId(_zoom,"LetItOut.Map.Zoom");AutomationProperties.SetAutomationId(_search,"LetItOut.Map.Search");
        _search.ToolTip=l("LetItOut.Tree.Search");AutomationProperties.SetName(_search,l("LetItOut.Tree.Search"));
        _search.SetResourceReference(Control.BackgroundProperty,"InputBackgroundBrush");_search.SetResourceReference(Control.ForegroundProperty,"TextPrimaryBrush");
        _search.SetResourceReference(Control.BorderBrushProperty,"LineBrush");_search.SetResourceReference(TextBox.CaretBrushProperty,"TextPrimaryBrush");
        _status.SetResourceReference(TextBlock.ForegroundProperty,"TextSecondaryBrush");
        DockPanel.SetDock(_status,Dock.Bottom);root.Children.Add(_status);
        var grid=new Grid();grid.ColumnDefinitions.Add(new());grid.ColumnDefinitions.Add(new(){Width=new(360)});root.Children.Add(grid);
        grid.Children.Add(_scroll);_scroll.Content=_canvas;
        var detailScroll=new ScrollViewer{Content=_detail,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};Grid.SetColumn(detailScroll,1);grid.Children.Add(detailScroll);
        _zoom.ValueChanged+=(_,_)=>Render();_search.KeyDown+=(_,e)=>{if(e.Key==Key.Enter)Find();};
        _scroll.PreviewMouseWheel+=(_,e)=>{_zoom.Value=Math.Clamp(_zoom.Value+(e.Delta>0?.12:-.12),.5,2.5);e.Handled=true;};
        _canvas.MouseLeftButtonDown+=(_,e)=>{if(e.OriginalSource==_canvas){_drag=e.GetPosition(_scroll);_x=_scroll.HorizontalOffset;_y=_scroll.VerticalOffset;_canvas.CaptureMouse();}};
        _canvas.MouseMove+=(_,e)=>{if(_drag is{}p){var at=e.GetPosition(_scroll);_scroll.ScrollToHorizontalOffset(_x+p.X-at.X);_scroll.ScrollToVerticalOffset(_y+p.Y-at.Y);}};
        _canvas.MouseLeftButtonUp+=(_,_)=>{_drag=null;_canvas.ReleaseMouseCapture();};
        Loaded+=(_,_)=>{if(focus is not null&&_nodes.ContainsKey(focus))Select(focus,true);else Render();};
    }
    private void Find()
    {
        var query=_search.Text.Trim();if(query.Length==0)return;
        var node=_nodes.Values.FirstOrDefault(n=>(n.Title+" "+n.Detail).Contains(query,StringComparison.OrdinalIgnoreCase));
        if(node is null){_status.Text=_l("LetItOut.Tree.NotFound");return;}Select(node.Id,true);
    }
    private void Render()
    {
        if(_nodes.Count==0)return;
        var root=_nodes.ContainsKey(_scope)?_scope:_nodes.Values.Single(n=>n.Parent=="").Id;
        var visible=new List<LetItOutTreeNode>();var positions=new Dictionary<string,Point>();var row=0;
        var depthLimit=_zoom.Value<.9?1:_zoom.Value<1.4?2:_zoom.Value<1.9?3:8;
        double Place(string id,int depth)
        {
            var node=_nodes[id];visible.Add(node);
            var children=depth<depthLimit?_nodes.Values.Where(n=>n.Parent==id).OrderByDescending(n=>n.Importance)
                .Take(_zoom.Value<.9?6:24).ToArray():[];
            var yy=children.Length==0?60+row++*148:children.Select(n=>Place(n.Id,depth+1)).Average();
            positions[id]=new(40+depth*310,yy);return yy;
        }
        Place(root,0);_positions=positions;_canvas.Children.Clear();_canvas.LayoutTransform=new ScaleTransform(_zoom.Value,_zoom.Value);
        _canvas.Width=Math.Max(800,positions.Values.Max(p=>p.X)+300);_canvas.Height=Math.Max(400,row*148+100);
        foreach(var node in visible.Where(n=>n.Id!=root))
        {
            var a=positions[node.Parent];var b=positions[node.Id];var line=new System.Windows.Shapes.Line{X1=a.X+240,Y1=a.Y+50,X2=b.X,Y2=b.Y+50,StrokeThickness=1.5};
            line.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,"TextSecondaryBrush");_canvas.Children.Add(line);
        }
        foreach(var node in visible)
        {
            var button=LetItOutMapNodeStyle.Create(node.Title,"LetItOut.Map.Node."+node.Id);button.Click+=(_,_)=>Select(node.Id,false);
            button.Width=240;button.Height=100;
            button.ToolTip=node.Detail;button.MouseDoubleClick+=(_,e)=>{Select(node.Id,true);e.Handled=true;};
            Canvas.SetLeft(button,positions[node.Id].X);Canvas.SetTop(button,positions[node.Id].Y);_canvas.Children.Add(button);
        }
        _status.Text=string.Format(_l("LetItOut.Tree.MapStatus"),visible.Count,_nodes.Count,_map.Through);
        AutomationProperties.SetAutomationId(_status,"LetItOut.Map.Status");
    }
    private void Select(string id,bool focus)
    {
        _selected=id;
        if(focus){_scope=id;_zoom.Value=1.2;}Render();_detail.Children.Clear();var node=_nodes[id];
        _detail.Children.Add(Label(node.Title,22));_detail.Children.Add(Label(_l("LetItOut.Tree.Status."+node.Status),12));
        _detail.Children.Add(Label(node.Detail,16));_detail.Children.Add(Action("LetItOut.Tree.Focus",()=>Select(id,true),"LetItOut.Map.Focus"));
        foreach(var child in _nodes.Values.Where(n=>n.Parent==id))_detail.Children.Add(ActionText(child.Title,()=>Select(child.Id,true),"LetItOut.Map.Child."+child.Id));
        foreach(var number in node.Sources)
        {
            var pair=_pairs.FirstOrDefault(p=>p.Number==number);if(pair is null)continue;
            _detail.Children.Add(Label(string.Format(_l("LetItOut.Tree.Source"),number,pair.UserUtc?.ToLocalTime().ToString("g")??""),12));
            _detail.Children.Add(Source(_l("LetItOut.You")+": "+pair.User));_detail.Children.Add(Source(_l("LetItOut.Assistant")+": "+pair.Answer));
        }
        if(focus&&_positions.TryGetValue(id,out var point))
        {_scroll.UpdateLayout();_scroll.ScrollToHorizontalOffset(Math.Max(0,point.X*_zoom.Value-80));_scroll.ScrollToVerticalOffset(Math.Max(0,(point.Y+50)*_zoom.Value-_scroll.ViewportHeight/2));}
    }
    private TextBox Source(string text)
    {var box=new TextBox{Text=text,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,Margin=new(0,6,0,6),Padding=new(8)};box.SetResourceReference(BackgroundProperty,"PanelBrush");box.SetResourceReference(ForegroundProperty,"TextPrimaryBrush");return box;}
    private static TextBlock Label(string text,int size)
    {var label=new TextBlock{Text=text,FontSize=size,TextWrapping=TextWrapping.Wrap,Margin=new(0,5,0,9)};label.SetResourceReference(TextBlock.ForegroundProperty,"TextPrimaryBrush");return label;}
    private Button Action(string key,Action action,string id)=>ActionText(_l(key),action,id);
    private static Button ActionText(string text,Action action,string id)
    {var b=new Button{Content=new TextBlock{Text=text,TextWrapping=TextWrapping.Wrap},Margin=new(3),Padding=new(9)};b.SetResourceReference(StyleProperty,"SecondaryButtonStyle");AutomationProperties.SetAutomationId(b,id);AutomationProperties.SetName(b,text);b.Click+=(_,_)=>action();return b;}
}
