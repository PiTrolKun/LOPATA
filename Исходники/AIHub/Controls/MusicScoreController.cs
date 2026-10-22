using System.IO;
using System.Security.Cryptography;
using System.Windows;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.MusicScore;
using AIHub.Services.MusicLora;
using MessageBox=System.Windows.MessageBox;

namespace AIHub.Controls;

public sealed class MusicScoreController : IDisposable
{
    private readonly MusicWorkspaceControl _view;
    private readonly MusicGenerationJobs _jobs;
    private MusicScoreWindow? _window;
    private int _lastTab;
    private Func<string,string> _l=k=>k;
    private string _modelsRoot="";
    private Task? _run;
    private bool _disposed;
    private bool _stopAfterCurrent;
    private readonly HashSet<string> _shownErrors=[];
    public event Action? Changed;
    public MusicScoreWorkspace State=>_view.Generation.Scores;
    public bool Running=>_run is {IsCompleted:false};
    public bool Busy=>_view.Projects.Busy&&!Running;
    public MusicExpertSettings Expert=>_view.Generation.ExpertSettings;
    public string Lyrics=>_view.Editor.Lyrics;
    public string Style=>_view.Wishes.RequestStyle;
    public MusicLoraUse[] Loras=>MusicLoraCatalog.Capture(_view.Lora.Selection,_view.Generation.Variation);
    public MusicScoreController(MusicWorkspaceControl view,MusicGenerationJobs jobs) {
        _view=view;_jobs=jobs;
        view.Generation.ScorePlansButton.Click+=(_,_)=>Open();
    }
    public void Configure(string root)=>_modelsRoot=root;
    public void Localize(Func<string,string> l)=>_l=l;
    public void Open(int? tab=null) {
        if(_disposed)return;
        if(MusicAceCatalog.IsAce(_view.Generation.Variation)){_view.AceIdeas.Open();return;}
        if(_window is not null){_window.Activate();if(tab is {} selected)_window.SelectTab(selected);return;}
        _window=new(this,_l){Owner=Window.GetWindow(_view)};
        var window=_window;
        window.Closed+=(_,_)=>{_lastTab=window.SelectedTab;_window=null;};
        window.SelectTab(tab??_lastTab);window.Show();
    }
    public void Update(MusicScoreWorkspace next) {
        if(Busy)throw new InvalidOperationException("Music.Generation.OtherOperation");
        if(next.Enabled&&next.Selected.Length==0)throw new InvalidDataException("Music.Score.NeedSelection");
        next.Validate();_view.Generation.Scores=next;_view.Projects.SaveScores(_view.Projects.Current.Id);Changed?.Invoke();
    }
    public void ReplaceDraft(string id,string draft) => Update(State with {Versions=State.Versions.Select(v=>v.Id==id?v with {Draft=draft}:v).ToArray()});
    public async Task<MusicScoreVersion> AddAsync(string abc,string origin,string? parent=null,string? name=null,MusicScoreVersion? provenance=null) {
        if(State.Versions.Length>=128)throw new InvalidDataException("Music.Score.HistoryFull");
        var old=State.Versions.FirstOrDefault(v=>v.Id==parent)??provenance;
        var cot=old?.Cot??(Expert.Cot=="off"?"full":Expert.Cot);
        // An explicit import/apply binds the score to the current song, never to an external path.
        _=await MusicScoreNative.InspectAsync(abc,ApplicationBackgroundOperations.ExitToken);
        var text=await MusicScoreNative.MarkAsync(abc,MusicLoraCatalog.EffectiveStyle(Style,Loras),Lyrics,cot,State.Keep,ApplicationBackgroundOperations.ExitToken);
        var version=new MusicScoreVersion {Parent=parent,Name=name??(State.Versions.Length+1).ToString(),Origin=origin,Abc=text,
            Hash=MusicScoreVersion.Stamp(text),Cot=cot,InputHash=MusicScoreVersion.Bind(Style,Lyrics,cot,Loras),
            RuntimeRevision=old?.RuntimeRevision,RuntimePack=old?.RuntimePack,ModelRevision=old?.ModelRevision,Seed=old?.Seed};
        Update(State with {Versions=[..State.Versions,version],Selected=[version.Id]});return version;
    }
    public async Task FromMidiAsync() {
        var melody=_view.Generation.Melody;
        if(melody.Notes.Length==0)throw new InvalidDataException("Music.Midi.Empty");
        await AddAsync(AIHub.Services.MusicMidi.MusicMelodyAbc.Build(melody),"midi",provenance:new(){Cot="melody"});
    }
    public async Task FromSongAsync() {
        var track=_view.Player.CurrentTrack;
        if(track?.JobId is null)throw new InvalidDataException("Music.Score.NoSongPlan");
        var job=_jobs.Load(track.JobId);var variant=job.Variants[track.Variant];
        var path=variant.EffectivePlanFile??variant.PlanFile;
        if(path is null)throw new InvalidDataException("Music.Score.NoSongPlan");
        var abc=await File.ReadAllTextAsync(path);
        var hash=variant.EffectivePlanHash??variant.PlanHash;
        if(MusicScoreVersion.Stamp(abc)!=hash)throw new InvalidDataException("Music.Score.Invalid");
        await AddAsync(abc,"song",name:track.Title,provenance:new(){Cot=job.Expert.Cot,RuntimeRevision=job.RuntimeRevision,RuntimePack=job.RuntimePack,
            ModelRevision=job.ModelRevision,Seed=variant.LanguageSeed});
    }
    public async Task PlanAsync(int count,string cot) {
        if(Running)return;
        if(State.Versions.Length+count>128)throw new InvalidDataException("Music.Score.HistoryFull");
        if(ApplicationBackgroundOperations.Current is {HasPending:true,State.Kind:MusicScorePlanWork.Kind} pending) {
            await pending.ResumeAsync(ApplicationBackgroundOperations.ExitToken);return;
        }
        if(ApplicationBackgroundOperations.Current?.BlocksNewWork==true)throw new InvalidOperationException("Music.Generation.OtherOperation");
        if(!MusicScorePipeline.Supported(_view.Generation.Variation))throw new InvalidDataException("Music.Score.Unsupported");
        var input=_view.Projects.Capture();var expert=input.Expert.Snapshot();expert.Values["cot"]=cot=="melody"?1:0;
        var flags=State with {Enabled=false,Selected=[],Versions=[],PendingWorkId=null};
        MusicScorePipeline.Validate(flags,MusicStudioRuntime.Variation,expert,null,null,input.Lyrics);
        input=input with {Expert=expert,Scores=flags,AudioTask=new(),Melody=new()};
        var profile=MusicStudioRuntime.SelectedProfile;
        var work=new MusicScorePlanRequest(Guid.NewGuid().ToString("N"),_view.Projects.Current.Id,_modelsRoot,input,Style,Loras,
            profile.Revision,profile.Pack,Enumerable.Range(0,Math.Clamp(count,1,8)).Select(i=>new MusicScoreCandidate(
                expert.Integer("lm_seed")<0?RandomNumberGenerator.GetInt32(1,int.MaxValue):(int)(((long)expert.Integer("lm_seed")+i)%int.MaxValue))).ToArray());
        MusicScorePlanWork.Save(work);Update(State with {PendingWorkId=work.Id});
        _run=RunAsync(work,null,ApplicationBackgroundOperations.ExitToken);await _run;
    }
    public Task ResumeAsync(BackgroundOperationState state,CancellationToken token) {
        var id=state.Input.GetProperty("Id").GetString()!;var work=MusicScorePlanWork.Load(id);
        if(_view.Projects.Current.Id!=work.ProjectId)_view.Projects.Open(work.ProjectId);
        _run=RunAsync(work,state,token);return _run;
    }
    public async Task PauseAsync() {
        if(ApplicationBackgroundOperations.Current is {State.Kind:MusicScorePlanWork.Kind} controller)await controller.PauseAsync();
    }
    private async Task RunAsync(MusicScorePlanRequest work,BackgroundOperationState? restored,CancellationToken token) {
        _view.Projects.SetBusy(true);Changed?.Invoke();
        _stopAfterCurrent=false;
        var worker=new MusicStudioWorker();worker.Log+=text=>_view.Dispatcher.Invoke(()=>_view.Status.AppendLog(text));
        try {
            await MusicScorePlanWork.RunAsync(work,worker,changed=>_view.Dispatcher.Invoke(()=>Accept(changed)),token,restored,()=>_stopAfterCurrent);
            Accept(MusicScorePlanWork.Load(work.Id));
        } catch(OperationCanceledException) { }
        catch(Exception error) {Error(error);}
        finally {_view.Projects.SetBusy(false);Changed?.Invoke();}
    }
    private void Accept(MusicScorePlanRequest work) {
        if(_view.Projects.Current.Id!=work.ProjectId)throw new InvalidDataException("Music.Score.InputChanged");
        var current=State;var versions=current.Versions.ToList();
        for(var index=0;index<work.Candidates.Length;index++) {
            var candidate=work.Candidates[index];
            if(candidate.Version is {} version && versions.All(v=>v.Id!=version.Id))versions.Add(version);
            if(candidate.Error is {} error) {
                var errorId=MusicScoreVersion.Stamp(work.Id+":"+index)[..32].ToLowerInvariant();
                if(versions.All(v=>v.Id!=errorId))versions.Add(new(){Id=errorId,Name=(index+1)+" · "+_l("Music.Score.PlanFailed"),Origin="generated",Abc="",Hash=MusicScoreVersion.Stamp(""),
                    InputHash=MusicScoreVersion.Bind(work.Style,work.Input.Lyrics,work.Input.Expert.Cot,work.Loras),Cot=work.Input.Expert.Cot,
                    WorkId=work.Id,WorkIndex=index,Seed=candidate.Seed,Error=error[..Math.Min(error.Length,4096)]});
                if(_shownErrors.Add(errorId))_view.Status.AppendLog(_l("Music.Score.PlanFailed")+" "+(error.StartsWith("Music.")?_l(error):error));
            }
        }
        var done=work.Candidates.All(c=>c.Version is not null||c.Error is not null);
        // Persist while our own operation holds the project busy. The project ID is checked above.
        _view.Generation.Scores=current with {Versions=versions.ToArray(),PendingWorkId=done?null:work.Id};
        _view.Projects.SaveScores(work.ProjectId);Changed?.Invoke();
    }
    public async Task StartSongsAsync() {
        if(Running) {
            if(MessageBox.Show(Window.GetWindow(_view),_l("Music.Score.StopThenSongs"),_l("Music.Score.Plans"),MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
            _stopAfterCurrent=true;await _run!;
        }
        if(ApplicationBackgroundOperations.Current?.BlocksNewWork==true)throw new InvalidOperationException("Music.Score.WaitPlans");
        var state=State with {Enabled=true};
        if(!EnsureMode(state))return;
        MusicScorePipeline.Validate(state,_view.Generation.Variation,Expert,_view.Generation.AudioTask,_view.Generation.Melody,Lyrics);
        var count=state.Selected.Length*_view.Generation.Options.Variants;
        if(count>8)throw new InvalidDataException("Music.Score.TooManySongs");
        if(MessageBox.Show(Window.GetWindow(_view),string.Format(_l("Music.Score.ConfirmSongs"),count),_l("Music.Score.Plans"),MessageBoxButton.OKCancel,MessageBoxImage.Question)!=MessageBoxResult.OK)return;
        Update(state);_window?.Close();var projectId=_view.Projects.Current.Id;
        await _view.Session.EnsureRuntimeReadyAsync();
        if(_view.Projects.Current.Id!=projectId||_view.Generation.Variation!=MusicStudioRuntime.Variation)throw new InvalidDataException("Music.Score.InputChanged");
        if(_view.Generation.StartPause is {} start)await start();
    }
    public bool ApplySelected() {
        var state=State with {Enabled=true};if(!EnsureMode(state))return false;
        MusicScorePipeline.Validate(state,_view.Generation.Variation,Expert,_view.Generation.AudioTask,_view.Generation.Melody,Lyrics);
        Update(state);return true;
    }
    private bool EnsureMode(MusicScoreWorkspace state) {
        if(!MusicScorePipeline.Supported(_view.Generation.Variation))throw new InvalidDataException("Music.Score.Unsupported");
        if(state.Selected.Length==0)throw new InvalidDataException("Music.Score.NeedSelection");
        var modes=state.Selected.Select(id=>state.Versions.Single(v=>v.Id==id).Cot).Distinct().ToArray();
        if(modes.Length!=1)throw new InvalidDataException("Music.Score.CotMismatch");
        if(Expert.Cot==modes[0])return true;
        if(MessageBox.Show(Window.GetWindow(_view),string.Format(_l("Music.Score.ChangeMode"),_l("Music.Score."+(modes[0]=="full"?"Full":"Melody"))),
            _l("Music.Score.Plans"),MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return false;
        var expert=Expert;expert.Values["cot"]=modes[0]=="full"?0:1;_view.Generation.SetExpertSettings(expert);return true;
    }
    public void Error(Exception error)=>MessageBox.Show(Window.GetWindow(_view),error.Message.StartsWith("Music.")?_l(error.Message):_l("Music.Score.Invalid")+"\n\n"+error.Message,
        _l("Music.Score.Plans"),MessageBoxButton.OK,MessageBoxImage.Warning);
    public void Dispose(){_disposed=true;_window?.Close();}
}
