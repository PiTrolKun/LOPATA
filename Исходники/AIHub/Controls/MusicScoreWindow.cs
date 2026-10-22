using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.MusicScore;
using Button=System.Windows.Controls.Button;
using CheckBox=System.Windows.Controls.CheckBox;
using ComboBox=System.Windows.Controls.ComboBox;
using ListBox=System.Windows.Controls.ListBox;
using TextBox=System.Windows.Controls.TextBox;
using Orientation=System.Windows.Controls.Orientation;
using TabControl=System.Windows.Controls.TabControl;
using SelectionMode=System.Windows.Controls.SelectionMode;
using FontFamily=System.Windows.Media.FontFamily;

namespace AIHub.Controls;

/// <summary>Local editing/preview window; model jobs belong to the common background controller.</summary>
public sealed class MusicScoreWindow : Window
{
    private readonly MusicScoreController _controller;
    private readonly Func<string,string> _l;
    private readonly ListBox _list=new(){SelectionMode=SelectionMode.Extended,MinWidth=180};
    private readonly TabControl _tabs=new();
    private readonly TextBox _abc=new(){AcceptsReturn=true,AcceptsTab=true,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,MinHeight=220,FontFamily=new FontFamily("Consolas")};
    private readonly TextBlock _summary=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(8)};
    private readonly TextBlock _status=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,8)};
    private readonly ComboBox _count=new(){ItemsSource=Enumerable.Range(1,8),SelectedItem=3,Width=65};
    private readonly ComboBox _cot=new(){Width=170};
    private readonly ComboBox _from=new(){MinWidth=120};
    private readonly ComboBox _to=new(){MinWidth=120};
    private readonly CheckBox _enabled=new(),_keep=new(),_follow=new(),_timing=new();
    private readonly MediaPlayer _player=new();
    private readonly CancellationTokenSource _lifetime=new();
    private readonly DispatcherTimer _draftTimer=new(){Interval=TimeSpan.FromMilliseconds(500)};
    private readonly string _previewFolder=Path.Combine(Path.GetTempPath(),"LOPATA-Score-"+Guid.NewGuid().ToString("N"));
    private string? _editing;
    private MusicScoreInspection? _inspection;
    private int _epoch;
    private bool _refreshing,_closed,_dirty;
    private bool _canSchedule;
    private string[] _listed=[];
    private string L(string key)=>_l("Music.Score."+key);
    public MusicScoreWindow(MusicScoreController controller,Func<string,string> localize) {
        _controller=controller;_l=localize;Title=L("Plans");Width=1080;Height=760;MinWidth=760;MinHeight=560;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        MusicWishUi.PrepareWindow(this,L("Plans"),"Music.Score.Window",1080);
        Height=760;MinWidth=760;MinHeight=560;
        Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("/AIHub;component/Controls/MusicScoreResources.xaml",UriKind.Relative)});
        SetResourceReference(BackgroundProperty,"WindowBackgroundBrush");SetResourceReference(ForegroundProperty,"TextPrimaryBrush");
        AutomationProperties.SetAutomationId(this,"Music.Score.Window");
        var root=new DockPanel{Margin=new Thickness(16)};Content=root;
        var footer=new StackPanel();DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);footer.Children.Add(_status);
        var actions=new WrapPanel();footer.Children.Add(actions);
        actions.Children.Add(Action("Apply",()=>{if(controller.ApplySelected())Close();}));
        actions.Children.Add(Action("Synthesize",async()=>await controller.StartSongsAsync()));
        actions.Children.Add(Action("Close",Close));
        var top=new StackPanel();DockPanel.SetDock(top,Dock.Top);root.Children.Add(top);
        top.Children.Add(new TextBlock{Text=L("Hint"),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,8)});
        var tools=new WrapPanel{Margin=new Thickness(0,0,0,8)};top.Children.Add(tools);
        _cot.Items.Add(L("Full"));_cot.Items.Add(L("Melody"));_cot.SelectedIndex=controller.Expert.Cot=="melody"?1:0;
        tools.Children.Add(_count);tools.Children.Add(_cot);
        tools.Children.Add(Action("GeneratePlans",async()=>await controller.PlanAsync((int)_count.SelectedItem,_cot.SelectedIndex==1?"melody":"full")));
        tools.Children.Add(Action("Pause",async()=>await controller.PauseAsync()));
        tools.Children.Add(Action("Import",ImportAsync));tools.Children.Add(Action("FromSong",async()=>await controller.FromSongAsync()));
        tools.Children.Add(Action("FromMidi",async()=>await controller.FromMidiAsync()));
        BindFlag(_enabled,"Enabled",s=>s.Enabled,(s,v)=>s with {Enabled=v});top.Children.Add(_enabled);
        var body=new Grid();body.ColumnDefinitions.Add(new(){Width=new GridLength(260)});body.ColumnDefinitions.Add(new());root.Children.Add(body);
        body.Children.Add(_list);Grid.SetColumn(_tabs,1);body.Children.Add(_tabs);_tabs.Margin=new Thickness(12,0,0,0);
        var overview=new StackPanel();overview.Children.Add(_summary);
        overview.Children.Add(Action("Preview",PreviewAsync));overview.Children.Add(Action("StopPreview",()=>_player.Close()));
        overview.Children.Add(new TextBlock{Text=L("PreviewHint"),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(8)});
        _tabs.Items.Add(new TabItem{Header=L("Plans"),Content=overview});
        var editor=new DockPanel();var editActions=new WrapPanel();DockPanel.SetDock(editActions,Dock.Bottom);editor.Children.Add(editActions);
        editActions.Children.Add(Action("Validate",async()=>await InspectAsync()));
        editActions.Children.Add(Action("SaveVersion",SaveVersionAsync));
        editActions.Children.Add(Action("Revert",()=>{if(Current is {} version){_abc.Text=version.Abc;FlushDraft();}}));
        editActions.Children.Add(Action("Export",Export));editor.Children.Add(_abc);
        _tabs.Items.Add(new TabItem{Header=L("Editor"),Content=editor});
        var settings=new StackPanel{Margin=new Thickness(8)};
        settings.Children.Add(new TextBlock{Text=L("KeepHint"),TextWrapping=TextWrapping.Wrap});
        BindFlag(_keep,"Keep",s=>s.Keep,(s,v)=>s with {Keep=v});settings.Children.Add(_keep);
        BindFlag(_follow,"Follow",s=>s.FollowSections,(s,v)=>s with {FollowSections=v});settings.Children.Add(_follow);
        BindFlag(_timing,"Timing",s=>s.LyricTiming,(s,v)=>s with {LyricTiming=v});settings.Children.Add(_timing);
        settings.Children.Add(new TextBlock{Text=L("TimingHint"),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,8)});
        settings.Children.Add(new TextBlock{Text=L("MirrorHint"),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,8)});
        var sections=new WrapPanel();sections.Children.Add(_from);sections.Children.Add(_to);sections.Children.Add(Action("Mirror",MirrorAsync));settings.Children.Add(sections);
        _tabs.Items.Add(new TabItem{Header=L("Sections"),Content=new ScrollViewer{Content=settings,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}});
        AutomationProperties.SetAutomationId(_abc,"Music.Score.Abc");AutomationProperties.SetAutomationId(_list,"Music.Score.List");
        _list.SelectionChanged+=async(_,_)=>{if(_refreshing)return;FlushDraft();var selected=_list.SelectedItems.Cast<MusicScoreVersion>().Where(v=>v.Error is null).Select(v=>v.Id).ToArray();
            try{controller.Update(controller.State with {Selected=selected,Enabled=controller.State.Enabled&&selected.Length>0});await LoadAsync(_list.SelectedItem as MusicScoreVersion);}catch(Exception e){controller.Error(e);}};
        _abc.TextChanged+=(_,_)=>{if(_refreshing)return;_dirty=true;_draftTimer.Stop();_draftTimer.Start();};
        _draftTimer.Tick+=(_,_)=>{_draftTimer.Stop();FlushDraft();};
        controller.Changed+=Refresh;
        Closing+=(_,_)=>FlushDraft();
        Closed+=(_,_)=>{_closed=true;_draftTimer.Stop();_lifetime.Cancel();controller.Changed-=Refresh;_player.Close();
            foreach(var path in Directory.Exists(_previewFolder)?Directory.GetFiles(_previewFolder,"*.wav"):[])try{File.Delete(path);}catch(IOException){} _lifetime.Dispose();};
        Refresh();
    }
    public void SelectTab(int index)=>_tabs.SelectedIndex=Math.Clamp(index,0,2);
    public int SelectedTab=>_tabs.SelectedIndex;
    private MusicScoreVersion? Current=>_controller.State.Versions.FirstOrDefault(v=>v.Id==_editing);
    private Button Action(string key,Action action) => Action(key,()=>{action();return Task.CompletedTask;});
    private Button Action(string key,Func<Task> action) {
        var button=new Button{Content=L(key),Margin=new Thickness(3),Padding=new Thickness(10,6,10,6)};
        AutomationProperties.SetAutomationId(button,"Music.Score."+key);
        button.Click+=async(_,_)=>{try{FlushDraft();await action();}catch(OperationCanceledException){}catch(Exception error){_controller.Error(error);}};return button;
    }
    private void BindFlag(CheckBox box,string name,Func<MusicScoreWorkspace,bool> read,Func<MusicScoreWorkspace,bool,MusicScoreWorkspace> write) {
        box.Content=L(name);box.Margin=new Thickness(0,6,0,6);AutomationProperties.SetAutomationId(box,"Music.Score."+name);
        box.Click+=(_,_)=>{if(_refreshing)return;try{
            if(name=="Enabled"&&box.IsChecked==true){if(!_controller.ApplySelected())box.IsChecked=read(_controller.State);}
            else _controller.Update(write(_controller.State,box.IsChecked==true));
        }catch(Exception error){box.IsChecked=read(_controller.State);_controller.Error(error);}};
    }
    private void Refresh() {
        if(_closed)return;
        _refreshing=true;
        try {
            var state=_controller.State;
            if(!_listed.SequenceEqual(state.Versions.Select(v=>v.Id))) {
                _listed=state.Versions.Select(v=>v.Id).ToArray();_list.ItemsSource=state.Versions.Reverse().ToArray();
                _list.DisplayMemberPath=nameof(MusicScoreVersion.Name);
                foreach(var version in state.Versions.Where(v=>state.Selected.Contains(v.Id)))_list.SelectedItems.Add(version);
            }
            _enabled.IsChecked=state.Enabled;_keep.IsChecked=state.Keep;_follow.IsChecked=state.FollowSections;_timing.IsChecked=state.LyricTiming;
            _timing.IsEnabled=state.LyricTiming||_controller.Expert.Get("cfg_scale")==1&&_canSchedule;
            _timing.ToolTip=L("TimingHint");ToolTipService.SetShowOnDisabled(_timing,true);
            _follow.IsEnabled=state.FollowSections||_controller.Expert.Cot=="full"&&System.Text.RegularExpressions.Regex.Matches(_controller.Lyrics,@"(?m)^\s*\[[^\]\r\n]+\]\s*$").Count is >=1 and <=64;
            _follow.ToolTip=L("FollowHint");ToolTipService.SetShowOnDisabled(_follow,true);
            _status.Text=string.Format(L(_controller.Running?"Progress":"Count"),state.Versions.Count(v=>v.Error is null),state.Selected.Length);
        } finally {_refreshing=false;}
        if(_list.SelectedItem is MusicScoreVersion selected&&selected.Id!=_editing&&!_dirty)_=LoadAsync(selected);
    }
    private void FlushDraft() {
        _draftTimer.Stop();if(!_dirty||_editing is null||_controller.Busy)return;
        if(Encoding.UTF8.GetByteCount(_abc.Text)>200000){_status.Text=L("TooLarge");return;}
        _dirty=false;_controller.ReplaceDraft(_editing,_abc.Text);
    }
    private async Task LoadAsync(MusicScoreVersion? version) {
        _player.Close();_editing=version?.Id;_inspection=null;_canSchedule=false;_refreshing=true;
        _abc.Text=version is null?"":version.Draft.Length>0?version.Draft:version.Abc;_dirty=false;_refreshing=false;
        await InspectAsync();
    }
    private async Task InspectAsync() {
        if(_editing is null)return;var epoch=++_epoch;var abc=_abc.Text;
        if(Current?.Error is {} failure&&string.IsNullOrWhiteSpace(abc)){
            _summary.Text=L("PlanFailed")+"\n"+(failure.StartsWith("Music.")?_l(failure):failure);_inspection=null;_canSchedule=false;Refresh();return;
        }
        try {
            var inspected=await MusicScoreNative.InspectAsync(abc,_lifetime.Token);
            var canSchedule=await MusicScoreNative.CanScheduleAsync(abc,_controller.Lyrics,_lifetime.Token);
            if(epoch!=_epoch||_closed)return;_inspection=inspected;var s=inspected.Sheet;
            _canSchedule=canSchedule;_timing.IsEnabled=_controller.State.LyricTiming||_controller.Expert.Get("cfg_scale")==1&&_canSchedule;
            _summary.Text=string.Format(L("Summary"),s.Bpm,s.Bars.FirstOrDefault()?.Meter??"?",s.Bars.FirstOrDefault()?.Key??"?",s.Seconds.ToString("0.#"),s.Notes.Vocal.Length,s.Notes.Ins.Length)
                +"\n\n"+L("Sections")+": "+string.Join(" → ",s.Sections.Select(p=>p.Name))
                +"\n"+L("Origin")+": "+L("Origin."+(Current?.Origin??"import"))+" · "+L("Created")+": "+Current?.Created.ToLocalTime().ToString("g");
            _from.ItemsSource=s.Sections.Select((section,i)=>new ComboBoxItem{Content=$"{i+1}. {section.Name}",Tag=i}).ToArray();
            _to.ItemsSource=s.Sections.Select((section,i)=>new ComboBoxItem{Content=$"{i+1}. {section.Name}",Tag=i}).ToArray();
            _from.SelectedIndex=0;_to.SelectedIndex=Math.Min(1,s.Sections.Length-1);
        }catch(OperationCanceledException){}catch(Exception error){if(epoch==_epoch&&!_closed){_inspection=null;_canSchedule=false;_summary.Text=L("Invalid")+"\n"+error.Message;Refresh();}}
    }
    private async Task PreviewAsync() {
        await InspectAsync();if(_inspection is null)return;
        _player.Close();Directory.CreateDirectory(_previewFolder);var path=Path.Combine(_previewFolder,Guid.NewGuid().ToString("N")+".wav");
        await Task.Run(()=>MusicScorePreview.Write(path,_inspection.Sheet,_lifetime.Token),_lifetime.Token);
        if(!_closed){_player.Open(new Uri(path));_player.Play();}
    }
    private async Task SaveVersionAsync() {if(Current is {} version)await _controller.AddAsync(_abc.Text,"edited",version.Id);}
    private async Task ImportAsync() {
        var dialog=new Microsoft.Win32.OpenFileDialog{Filter="ABC|*.abc;*.txt"};if(dialog.ShowDialog(this)!=true)return;
        if(new FileInfo(dialog.FileName).Length>200000)throw new InvalidDataException("Music.Score.TooLarge");
        var abc=await File.ReadAllTextAsync(dialog.FileName,new UTF8Encoding(false,true),_lifetime.Token);
        abc=string.Join("\n",abc.Split('\n').Where(line=>!line.StartsWith("%lopata-origin ",StringComparison.Ordinal)));
        await _controller.AddAsync(abc,"import",name:Path.GetFileNameWithoutExtension(dialog.FileName));
    }
    private async Task MirrorAsync() {
        if(_from.SelectedItem is not ComboBoxItem from||_to.SelectedItem is not ComboBoxItem to||Current is not {} version)return;
        var result=await MusicScoreNative.MirrorAsync(_abc.Text,(int)from.Tag,[(int)to.Tag],_lifetime.Token);
        // Preview only. Applying it creates a new version through the ordinary editor action.
        _abc.Text=result.Abc;_tabs.SelectedIndex=1;await InspectAsync();_status.Text=L("MirrorPreview");
    }
    private void Export() {
        if(Current is not {} version)return;
        var dialog=new Microsoft.Win32.SaveFileDialog{Filter="ABC|*.abc",FileName="Score.abc"};if(dialog.ShowDialog(this)!=true)return;
        var origin=JsonSerializer.Serialize(new {application="LOPATA",version=MusicSongMetadata.AppVersion,operation="music.score.export",created=DateTimeOffset.Now,
            source_id=version.Id,source_hash=version.Hash,runtime_revision=version.RuntimeRevision,model=version.ModelRevision is null?null:MusicStudioRuntime.Variation,
            model_revision=version.ModelRevision,role="score-planner",history=version.RuntimeRevision is null?"unknown":"recorded"});
        File.WriteAllText(dialog.FileName,_abc.Text.TrimEnd()+"\n%lopata-origin "+origin+"\n",new UTF8Encoding(false));
    }
}
