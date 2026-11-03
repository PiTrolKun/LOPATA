using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AIHub.Models;
using Button=System.Windows.Controls.Button;
using UserControl=System.Windows.Controls.UserControl;
using Point=System.Windows.Point;

namespace AIHub.Controls;

internal sealed class LetItOutTopicTreePanel:UserControl
{
    private readonly StackPanel _body=new(){Margin=new(10)};
    internal event Action<string?>? OpenRequested;
    internal event Action? RetryRequested;
    internal LetItOutTopicTreePanel(){Content=new ScrollViewer{Content=_body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};AutomationProperties.SetAutomationId(this,"LetItOut.TopicTree");}
    internal void Update(LetItOutTreeDocument? document,Func<string,string> l,bool working,bool failed)
    {
        _body.Children.Clear();Add(l("LetItOut.Tree.Title"),18);
        if(working)Add(l("LetItOut.Tree.Building"),12);
        if(failed)
        {
            Add(l("LetItOut.Tree.Failed"),12);var retry=Button(l("LetItOut.Tree.Retry"),"LetItOut.Tree.Retry");
            retry.Click+=(_,_)=>RetryRequested?.Invoke();_body.Children.Add(retry);
        }
        if(document is null){Add(l("LetItOut.Tree.Empty"),13);return;}
        Add(string.Format(l("LetItOut.Tree.Through"),document.Through),12);
        _body.Children.Add(DrawTree(document));
        var open=Button(l("LetItOut.Tree.Open"),"LetItOut.Tree.Open");open.Click+=(_,_)=>OpenRequested?.Invoke(null);_body.Children.Add(open);
        Add(l("LetItOut.Tree.Disclaimer"),11);
    }
    private FrameworkElement DrawTree(LetItOutTreeDocument document)
    {
        var root=document.Nodes.Single(n=>n.Parent=="");var visible=new List<LetItOutTreeNode>();
        var positions=new Dictionary<string,Point>();var leaf=0;var depthMax=0;
        double Place(LetItOutTreeNode node,int depth,int branches)
        {
            visible.Add(node);depthMax=Math.Max(depthMax,depth);
            var children=depth<3?document.Nodes.Where(n=>n.Parent==node.Id&&n.Status!="corrected")
                .OrderByDescending(n=>n.Importance).ThenByDescending(n=>n.Sources.DefaultIfEmpty().Max()).Take(branches).ToArray():[];
            var x=children.Length==0?65+130*leaf++:children.Select(n=>Place(n,depth+1,children.Length==1?branches:1)).Average();
            positions[node.Id]=new(x,16+depth*124);return x;
        }
        Place(root,0,2);var canvas=new Canvas{Width=260,Height=32+(depthMax+1)*124};
        var shift=leaf==1?65:0;
        foreach(var node in visible.Where(n=>n.Id!=root.Id))
        {
            var a=positions[node.Parent];var b=positions[node.Id];
            var geometry=new PathGeometry([new PathFigure(new(a.X+shift,a.Y+92),[new BezierSegment(new(a.X+shift,a.Y+110),new(b.X+shift,b.Y-18),new(b.X+shift,b.Y),true)],false)]);
            var line=new System.Windows.Shapes.Path{Data=geometry,StrokeThickness=1.5};
            line.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,"TextSecondaryBrush");canvas.Children.Add(line);
        }
        foreach(var node in visible)
        {
            var button=LetItOutMapNodeStyle.Create(node.Title.Length>55?node.Title[..52]+"…":node.Title,"LetItOut.Tree.Node."+node.Id,11.5);
            button.Width=node.Id==root.Id?144:108;button.Height=92;button.ToolTip=node.Title+"\n"+node.Detail;
            AutomationProperties.SetName(button,node.Title);button.Click+=(_,_)=>OpenRequested?.Invoke(node.Id);
            Canvas.SetLeft(button,positions[node.Id].X+shift-button.Width/2);Canvas.SetTop(button,positions[node.Id].Y);canvas.Children.Add(button);
        }
        AutomationProperties.SetAutomationId(canvas,"LetItOut.Tree.Graph");
        return new Viewbox{Child=canvas,Stretch=Stretch.Uniform,HorizontalAlignment=System.Windows.HorizontalAlignment.Stretch};
    }
    private void Add(string text,int size)
    {var label=new TextBlock{Text=text,FontSize=size,TextWrapping=TextWrapping.Wrap,Margin=new(2,4,2,10)};label.SetResourceReference(TextBlock.ForegroundProperty,"TextSecondaryBrush");_body.Children.Add(label);}
    private static Button Button(string text,string id)
    {
        var b=new Button{Content=new TextBlock{Text=text,TextWrapping=TextWrapping.Wrap},Margin=new(0,5,0,5),Padding=new(9),HorizontalContentAlignment=System.Windows.HorizontalAlignment.Left};
        b.SetResourceReference(StyleProperty,"SecondaryButtonStyle");AutomationProperties.SetAutomationId(b,id);AutomationProperties.SetName(b,text);return b;
    }
}
