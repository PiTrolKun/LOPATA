using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.SongClip;
using Button=System.Windows.Controls.Button;
using ComboBox=System.Windows.Controls.ComboBox;
using ListBox=System.Windows.Controls.ListBox;
using ListBoxItem=System.Windows.Controls.ListBoxItem;
using ProgressBar=System.Windows.Controls.ProgressBar;
using Brushes=System.Windows.Media.Brushes;
using Color=System.Windows.Media.Color;
using Control=System.Windows.Controls.Control;
using TextBox=System.Windows.Controls.TextBox;
using UserControl=System.Windows.Controls.UserControl;
using Orientation=System.Windows.Controls.Orientation;

namespace AIHub.Controls;

/// <summary>Song workspace owns presentation; runtimes and durable project state live in services.</summary>
public sealed partial class SongClipControl:UserControl
{
    private Func<string,string> _l;
    private readonly SongClipStore _store=new();
    private SongClipProject _project=FreshProject();
    private readonly TextBox _name=new(),_lyrics=new(),_timings=new(),_prompt;
    private readonly ExampleTextInput _promptInput;
    private readonly Button _model=new();
    private readonly ComboBox _size=new(),_steps=new(),_interval=new();
    private readonly ComboBox _motionSize=new(),_motionSteps=new();
    private readonly ListBox _results=new();
    private readonly MediaElement _preview=new(){LoadedBehavior=MediaState.Manual,UnloadedBehavior=MediaState.Close,Stretch=Stretch.Uniform,IsMuted=true};
    private readonly TextBlock _status=new(){TextWrapping=TextWrapping.Wrap},_reference=new(){TextWrapping=TextWrapping.Wrap};
    private readonly TextBox _log=new(){IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    private readonly ProgressBar _progress=new(){Height=12,Maximum=100};
    private readonly DispatcherTimer _saveTimer=new(){Interval=TimeSpan.FromMilliseconds(700)};
    private readonly DispatcherTimer _clock=new(){Interval=TimeSpan.FromSeconds(1)};
    private Button? _generate,_pause,_stop;
    private string _root="";private int _connections;private bool _busy,_loading,_playing,_backgroundAttached,_stopRequested;
    private CancellationTokenSource? _cancel;
    private DateTimeOffset _started;private double _fraction;
    private string _stage="";
    public event Action? HomeRequested,BackRequested;
    public event Action<string>? EditorRequested;
    public SongClipControl(Func<string,string> localize) : this(localize,new SongClipStore()) { }
    internal SongClipControl(Func<string,string> localize,SongClipStore store)
    {
        _l=localize;_store=store;
        _promptInput=new(_l("SongClip.Prompt"),_l("SongClip.PromptExample"),"SongClip.Prompt",multiline:true);
        _prompt=_promptInput.Input;_prompt.Height=double.NaN;
        _prompt.Loaded+=(_,_)=>LiterarySpellChecking.Enable(_prompt,_l("VideoPrep.SpellingLanguage"));
        Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("/AIHub;component/Controls/SongClipResources.xaml",UriKind.Relative)});
        SetResourceReference(ForegroundProperty,"TextPrimaryBrush");
        _saveTimer.Tick+=(_,_)=>{_saveTimer.Stop();if(!_busy)Checkpoint();};
        _clock.Tick+=(_,_)=>UpdateStatus();
        Build();LoadFields();
        foreach(var box in new[]{_name,_lyrics,_timings,_prompt})box.TextChanged+=(_,_)=>{if(!_loading&&!_busy){_saveTimer.Stop();_saveTimer.Start();}};
        foreach(var choice in new[]{_size,_steps,_interval,_motionSize,_motionSteps,_montageClipSeconds})choice.SelectionChanged+=(_,_)=>{if(!_loading&&!_modeSettingsLoading&&!_busy)Checkpoint();};
        _h3Mode.SelectionChanged+=(_,_)=>ChangeClipMode();
        _results.SelectionChanged+=(_,_)=>ShowResult();
        IsVisibleChanged+=(_,_)=>{if(!IsVisible)SuspendPreview();};
        Unloaded+=(_,_)=>{SuspendPreview();Checkpoint();LiterarySpellChecking.ReleaseTree(_promptInput);LiterarySpellChecking.ReleaseTree(_referenceMeaningPanel);};
    }
    public void Configure(Func<string,string> l,string root,int connections){
        _l=l;_root=root;_connections=connections;
        if(!_backgroundAttached&&ApplicationBackgroundOperations.Current is {} controller){controller.Changed+=()=>Dispatcher.BeginInvoke(RefreshActions);_backgroundAttached=true;}
        RecoverInvalidWaitingAttempt();
    }
    public void OpenProject(string id){if(_busy||!Checkpoint())return;_project=_store.Load(id);LoadFields();RecoverInvalidWaitingAttempt();}
    public bool Checkpoint()
    {
        if(_loading)return true;
        try{Capture();_store.SaveDraft(_project);return true;}catch(Exception error){_status.Text=error.Message;return false;}
    }
    private void Capture()
    {
        _project.Name=_name.Text;_project.Lyrics=_lyrics.Text;_project.Timings=_timings.Text;_project.Settings.Prompt=_prompt.Text;
        if(_size.SelectedIndex>=0){var sizes=new[]{(640,360),(1280,720),(1920,1080),(720,1280)};var size=sizes[_size.SelectedIndex];_project.Settings.Width=size.Item1;_project.Settings.Height=size.Item2;}
        if(_steps.SelectedItem is int steps)_project.Settings.Steps=steps;
        if(_interval.SelectedItem is int interval)_project.Settings.Interval=interval;
        CaptureMotionSettings();
    }
    private void LoadFields()
    {
        _loading=true;
        try{
            _name.Text=_project.Name;_lyrics.Text=_project.Lyrics;_timings.Text=_project.Timings;_prompt.Text=_project.Settings.Prompt;
            _size.SelectedIndex=_project.Settings.Width==1920?2:_project.Settings.Height==1280?3:_project.Settings.Width==640?0:1;
            _steps.SelectedItem=_project.Settings.Steps;_interval.SelectedItem=_project.Settings.Interval;
            LoadMotionSettings();RefreshEnginePresentation();
            RefreshReference();
            _results.ItemsSource=_project.Results.Select(r=>new ListBoxItem{Content=ResultLabel(r),Tag=r}).ToArray();
            if(_results.Items.Count>0)_results.SelectedIndex=_results.Items.Count-1;
            ShowResult();
            RefreshActions();
        }finally{_loading=false;}
    }
    private bool OwnOperation=>OwnOperationState is not null;
    private void RefreshActions()=>RefreshGenerationActions();
    private Button Button(string key,Action action,string id="")
    {
        var b=new Button{Content=_l("SongClip."+key),Margin=new(3),Padding=new(10,7,10,7),VerticalAlignment=VerticalAlignment.Center};
        AutomationProperties.SetName(b,_l("SongClip."+key));
        if(id.Length>0)AutomationProperties.SetAutomationId(b,"SongClip."+id);b.Click+=(_,_)=>action();return b;
    }
    private TextBlock Label(string key)=>new(){Text=_l("SongClip."+key),Margin=new(3,5,3,5),TextWrapping=TextWrapping.Wrap};
    private Border Panel(UIElement child){var b=new Border{Child=child,Padding=new(10),Margin=new(5),CornerRadius=new(8)};b.SetResourceReference(Border.BackgroundProperty,"PanelBrush");return b;}
    private static void Multiline(TextBox box,string id){box.AcceptsReturn=true;box.TextWrapping=TextWrapping.Wrap;box.VerticalScrollBarVisibility=ScrollBarVisibility.Auto;AutomationProperties.SetAutomationId(box,"SongClip."+id);}
    private async Task ImportSong()
    {
        if(_busy)return;var dialog=new Microsoft.Win32.OpenFileDialog{Filter="Audio|*.wav;*.mp3;*.flac;*.m4a;*.ogg;*.opus|All files|*.*"};if(dialog.ShowDialog(Window.GetWindow(this))!=true||!Checkpoint())return;
        _busy=true;SetInputs(false);RefreshActions();
        try{
            var next=await SongClipImport.AddSongAsync(_store,_project,dialog.FileName,ApplicationBackgroundOperations.ExitToken);
            var metadata=await SongClipSourceMetadata.ReadAsync(_store.Resolve(next.Id,next.Song),ApplicationBackgroundOperations.ExitToken);
            if(next.Lyrics.Length==0)next.Lyrics=metadata.Lyrics;
            if(next.Timings.Length==0)next.Timings=metadata.Timings;
            AdoptImportedSong(next);_status.Text=_l("SongClip.SongReady");
        }catch(Exception error){_status.Text=error.Message;}
        finally{_busy=false;SetInputs(true);LoadFields();}
    }
    private void ImportLrc(){if(_busy)return;var d=new Microsoft.Win32.OpenFileDialog{Filter="Lyrics|*.lrc;*.txt"};if(d.ShowDialog(Window.GetWindow(this))==true){try{_timings.Text=File.ReadAllText(d.FileName);Checkpoint();}catch(Exception e){_status.Text=e.Message;}}}
    private SongClipResult? SelectedResult()=>(_results.SelectedItem as ListBoxItem)?.Tag as SongClipResult;
    private void Export(){if(SelectedResult() is not {} r)return;var d=new Microsoft.Win32.SaveFileDialog{Filter="MP4|*.mp4",FileName=(_project.Name.Length>0?_project.Name:"clip")+".mp4"};if(d.ShowDialog(Window.GetWindow(this))==true){try{File.Copy(_store.Resolve(_project.Id,r.File),d.FileName,true);_status.Text=_l("SongClip.Saved");}catch(Exception e){_status.Text=e.Message;}}}
    private void Manage()
    {
        if(_busy||!Checkpoint())return;
        try{var list=new ListBox();foreach(var p in _store.List())list.Items.Add(new ListBoxItem{Content=(p.Pinned?"★ ":"")+(p.Name.Length==0?_l("SongClip.Draft"):p.Name)+" · "+p.UpdatedUtc.ToLocalTime().ToString("g"),Tag=p.Id});
            var w=new Window{Owner=Window.GetWindow(this)};MusicWishUi.PrepareWindow(w,_l("SongClip.Projects"),"SongClip.Projects.Window",650);w.Height=450;
            var panel=new DockPanel{Margin=new(15)};var open=Button("Open",()=>{if(list.SelectedItem is ListBoxItem i){_project=_store.Load((string)i.Tag);w.DialogResult=true;LoadFields();RecoverInvalidWaitingAttempt();}});DockPanel.SetDock(open,Dock.Bottom);panel.Children.Add(open);panel.Children.Add(list);w.Content=panel;w.ShowDialog();
        }catch(Exception error){_status.Text=error.Message;}
    }
    private async Task StartAsync(SongClipAttempt? restoredAttempt=null,BackgroundOperationState? restored=null,CancellationToken token=default)
    {
        try{
            if(SongClipVariantCatalog.IsRetiredModel(restoredAttempt?.Model??_project.Model)) {
                _status.Text=_l(SongClipVariantCatalog.RetiredMessageKey);return;
            }
            RecoverInvalidWaitingAttempt();
            if(restored is null&&OwnOperationState is {} own&&ApplicationBackgroundOperations.Current is {} c){if(CanResume(own)){await c.RetryPausedRetirementAsync(ApplicationBackgroundOperations.RetireModelsAsync);await c.ResumeAsync(own.Id,ApplicationBackgroundOperations.ExitToken);}return;}
            await StartCoreAsync(restoredAttempt,restored,token);
        }
        catch(Exception error){_status.Text=FormatEngineProgress(error.Message);}
    }
    private async Task StartCoreAsync(SongClipAttempt? restoredAttempt,BackgroundOperationState? restored,CancellationToken token)
    {
        if(_busy||!Checkpoint()||_project.Song.Length==0)return;
        if(restored is null&&ApplicationBackgroundOperations.Current is { BlocksNewWork:true } background){background.ReportBlocked();_status.Text=_l("SongClip.OtherOperation");return;}
        var attempt=SelectStartAttempt(restoredAttempt);
        if(!TryValidateStartAttempt(attempt))return;
        if (!SongClipAutomaticPreparation.PrepareIfNeeded(attempt, variant =>
            new SongClipDownloadWindow(Window.GetWindow(this), _l, attempt.ModelsRoot, attempt.Connections,
                variant.Model, variant.Mode, autoDownload: true).ShowDialog() == true)) return;
        _store.EnsureAttemptSnapshot(attempt);
        _project.PendingAttemptId=attempt.Id;_store.Save(_project);
        _cancel=CancellationTokenSource.CreateLinkedTokenSource(token);_busy=true;_stopRequested=false;_deviceSummary="";SetInputs(false);RefreshActions();_started=DateTimeOffset.UtcNow;_fraction=0;_clock.Start();
        try{
            var result=await ApplicationBackgroundOperations.RunAsync(attempt.Model==InfiniteTalkRunner.ModelId?InfiniteTalkRunner.Kind:attempt.Model=="h3_music_context"?H3MusicRunner.Kind:SongClipAttempt.Kind,EngineName(attempt.Model),attempt.ProjectId,attempt,
                async ct=>await RunEngineAsync(attempt,(fraction,message)=>Dispatcher.BeginInvoke(()=>{_fraction=Math.Max(_fraction,fraction);_stage=FormatEngineProgress(message);_log.AppendText(_stage+Environment.NewLine);if(_log.Text.Length>32000)_log.Text=_log.Text[^16000..];_log.ScrollToEnd();UpdateStatus();}),ct),_cancel.Token,restored);
            if(!_project.Results.Any(r=>r.Id==result.Id))_project.Results.Add(result);_project.PendingAttemptId="";_store.Save(_project);_stage=_l("SongClip.Complete");_fraction=1;
        }catch(OperationCanceledException){if(_stopRequested){_project.PendingAttemptId="";_store.Save(_project);}_stage=_l("SongClip.Canceled");}
        catch(Exception error){_stage=FormatEngineProgress(error.Message);}
        finally{_busy=false;SetInputs(true);_clock.Stop();_cancel.Dispose();_cancel=null;LoadFields();UpdateStatus();}
    }
    public async Task ResumeAsync(BackgroundOperationState state,CancellationToken token)
    {
        var attempt=state.Input.Deserialize<SongClipAttempt>()??throw new InvalidDataException("Missing song-clip checkpoint.");
        if(SongClipVariantCatalog.IsRetiredModel(attempt.Model))throw new InvalidOperationException(_l(SongClipVariantCatalog.RetiredMessageKey));
        _project=_store.Load(attempt.ProjectId);LoadFields();if(RecoverInvalidWaitingAttempt())return;await StartAsync(attempt,state,token);
    }
    private void UpdateStatus(){_progress.Value=_fraction*100;if(OwnOperationState is {Phase:BackgroundOperationPhase.Paused}){_status.Text=_l("SongClip.Paused");return;}var elapsed=DateTimeOffset.UtcNow-_started;_status.Text=(_busy&&_deviceSummary.Length>0&&_stage!=_deviceSummary?_deviceSummary+" · ":"")+_stage+(_busy?$" · {elapsed:hh\\:mm\\:ss} · {_fraction:P0}":"");}
    private void SetInputs(bool enabled){foreach(var c in new Control[]{_name,_lyrics,_timings,_prompt,_model,_size,_steps,_interval,_motionSize,_motionSteps,_h3Mode,_montageClipSeconds,_geekSize,_geekSteps,_geekCardSteps,_geekSeconds,_geekStrength,_infiniteSize,_infiniteSteps,_infiniteAudioCfg,_infiniteVramReserve})c.IsEnabled=enabled;SetReferenceInputs(enabled);}
}
