using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services.MusicAceIdeas;
using Button=System.Windows.Controls.Button;
using CheckBox=System.Windows.Controls.CheckBox;
using ComboBox=System.Windows.Controls.ComboBox;
using ListBox=System.Windows.Controls.ListBox;
using Orientation=System.Windows.Controls.Orientation;
using SelectionMode=System.Windows.Controls.SelectionMode;

namespace AIHub.Controls;

public sealed class MusicAceIdeaWindow:Window
{
    private readonly MusicAceIdeaController _controller;
    private readonly Func<string,string> _l;
    private readonly ListBox _list=new(){SelectionMode=SelectionMode.Extended,DisplayMemberPath=nameof(MusicAceIdea.Name)};
    private readonly TextBlock _details=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(10)};
    private readonly TextBlock _status=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,8)};
    private readonly CheckBox _enabled=new(),_retake=new();
    private readonly Slider _variance=new(){Minimum=0,Maximum=1,TickFrequency=.05,SmallChange=.05,IsSnapToTickEnabled=true,Margin=new Thickness(6,12,6,12)};
    private readonly TextBlock _amount=new();
    private string[] _listed=[];
    private bool _refreshing;
    public MusicAceIdeaWindow(MusicAceIdeaController controller,Func<string,string> localize) {
        _controller=controller;_l=localize;MusicWishUi.PrepareWindow(this,L("Title"),"Music.AceIdeas.Window",1040);MinWidth=760;MinHeight=560;Height=740;
        Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("/AIHub;component/Controls/MusicScoreResources.xaml",UriKind.Relative)});
        var root=new DockPanel{Margin=new Thickness(16)};Content=root;
        var top=new StackPanel();DockPanel.SetDock(top,Dock.Top);root.Children.Add(top);
        top.Children.Add(new TextBlock{Text=L("Hint"),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,10)});
        var tools=new WrapPanel();top.Children.Add(tools);
        var count=new ComboBox{ItemsSource=Enumerable.Range(1,8),SelectedItem=3,Width=65,Margin=new Thickness(3)};
        AutomationProperties.SetAutomationId(count,"Music.AceIdeas.Count");AutomationProperties.SetName(count,L("PlanCount"));tools.Children.Add(count);
        tools.Children.Add(Action("Generate",()=>controller.PlanAsync((int)count.SelectedItem)));
        tools.Children.Add(Action("Pause",controller.PauseAsync));tools.Children.Add(Action("FromSong",controller.FromSongAsync));
        _enabled.Content=L("Enabled");_enabled.Margin=new Thickness(0,10,0,10);top.Children.Add(_enabled);
        AutomationProperties.SetAutomationId(_enabled,"Music.AceIdeas.Enabled");
        _enabled.Click+=(_,_)=>Guard(()=>{if(_enabled.IsChecked==true)controller.Apply();else controller.Update(controller.State with {Enabled=false});});
        var footer=new StackPanel();DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);footer.Children.Add(_status);
        var actions=new WrapPanel();footer.Children.Add(actions);
        actions.Children.Add(Action("Apply",()=>{controller.Apply();Close();}));
        actions.Children.Add(Action("Preview",()=>controller.RenderAsync(true)));actions.Children.Add(Action("Render",()=>controller.RenderAsync(false)));
        actions.Children.Add(Action("Close",Close));
        var body=new Grid();body.ColumnDefinitions.Add(new(){Width=new GridLength(240)});body.ColumnDefinitions.Add(new());root.Children.Add(body);
        body.Children.Add(_list);AutomationProperties.SetAutomationId(_list,"Music.AceIdeas.List");AutomationProperties.SetName(_list,L("Title"));
        var right=new StackPanel{Margin=new Thickness(12,0,0,0)};
        var scroll=new ScrollViewer{Content=right,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};Grid.SetColumn(scroll,1);body.Children.Add(scroll);
        right.Children.Add(_details);right.Children.Add(Action("Restore",()=>controller.RestoreSelected()));
        _retake.Content=L("Retake");_retake.Margin=new Thickness(0,12,0,4);right.Children.Add(_retake);
        AutomationProperties.SetAutomationId(_retake,"Music.AceIdeas.Retake");
        right.Children.Add(new TextBlock{Text=L("RetakeHint"),TextWrapping=TextWrapping.Wrap});right.Children.Add(_variance);right.Children.Add(_amount);
        AutomationProperties.SetAutomationId(_variance,"Music.AceIdeas.Variance");AutomationProperties.SetName(_variance,L("Variance"));
        _retake.Click+=(_,_)=>Guard(()=>controller.Update(controller.State with {Retake=_retake.IsChecked==true}));
        _variance.ValueChanged+=(_,_)=>{if(!_refreshing)Guard(()=>controller.Update(controller.State with {Variance=_variance.Value}));};
        _list.SelectionChanged+=(_,_)=>{if(_refreshing)return;Guard(()=>{var ids=_list.SelectedItems.Cast<MusicAceIdea>().Select(i=>i.Id).ToArray();
            controller.Update(controller.State with {Selected=ids,Enabled=controller.State.Enabled&&ids.Length>0});});};
        controller.Changed+=Refresh;Closed+=(_,_)=>controller.Changed-=Refresh;Refresh();
    }
    private string L(string key)=>_l("Music.AceIdeas."+key);
    private Button Action(string key,Action action)=>Action(key,()=>{action();return Task.CompletedTask;});
    private Button Action(string key,Func<Task> action) {
        var button=MusicWishUi.Button(L(key),"Music.AceIdeas."+key,()=>{});button.Padding=new Thickness(10,7,10,7);
        button.Click+=async(_,_)=>{try{await action();}catch(OperationCanceledException){}catch(Exception error){_controller.Error(error);Refresh();}};return button;
    }
    private void Guard(Action action){try{action();}catch(Exception error){_controller.Error(error);Refresh();}}
    private void Refresh() {
        _refreshing=true;
        try {
            var state=_controller.State;
            if(!_listed.SequenceEqual(state.Ideas.Select(i=>i.Id))) {
                _listed=state.Ideas.Select(i=>i.Id).ToArray();_list.ItemsSource=state.Ideas.Reverse().ToArray();
            }
            var selected=_list.SelectedItems.Cast<MusicAceIdea>().Select(i=>i.Id).ToArray();
            if(!selected.Order().SequenceEqual(state.Selected.Order())) {
                _list.SelectedItems.Clear();foreach(var idea in state.Ideas.Where(i=>state.Selected.Contains(i.Id)))_list.SelectedItems.Add(idea);
            }
            var current=_list.SelectedItem as MusicAceIdea;
            _details.Text=current is null?L("SelectHint"):string.Format(L("Summary"),current.Data.Bpm?.ToString()??"—",current.Data.Keyscale,current.Data.Timesignature,
                current.Data.Duration.ToString("0.#"),L(current.Origin=="song"?"OriginSong":"OriginGenerated"),current.Created.ToLocalTime().ToString("g"))+"\n\n"+current.Data.Caption;
            _enabled.IsChecked=state.Enabled;_retake.IsChecked=state.Retake;_variance.Value=state.Variance;_variance.IsEnabled=state.Retake;
            _amount.Text=string.Format(L("VarianceValue"),state.Variance.ToString("P0"));
            _status.Text=string.Format(L(_controller.Running?"Progress":"Status"),state.Ideas.Length,state.Selected.Length)
                +string.Concat(state.Errors.Concat(_controller.LastError is {} last?[last]:[]).Distinct().Select(error=>"\n"+(error.StartsWith("Music.")?_l(error):error)));
        }finally{_refreshing=false;}
    }
}
