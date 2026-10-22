using System.IO;
using System.Security.Cryptography;
using System.Windows;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.MusicAceIdeas;
using AIHub.Services.MusicLora;
using MessageBox=System.Windows.MessageBox;

namespace AIHub.Controls;

public sealed class MusicAceIdeaController:IDisposable
{
    private readonly MusicWorkspaceControl view;
    private readonly MusicGenerationJobs jobs;
    public MusicAceIdeaController(MusicWorkspaceControl workspace,MusicGenerationJobs generationJobs) {
        view=workspace;jobs=generationJobs;view.Generation.OptionsChanged+=OptionsChanged;
    }
    private void OptionsChanged()=>Changed?.Invoke();
    private Func<string,string> _l=k=>k;
    private string _modelsRoot="";
    private MusicAceIdeaWindow? _window;
    private Task? _run;
    private bool _disposed;
    public event Action? Changed;
    public MusicAceIdeaWorkspace State=>view.Generation.AceIdeas;
    public bool Running=>_run is {IsCompleted:false};
    public string? LastError {get;private set;}
    public void Configure(string modelsRoot)=>_modelsRoot=modelsRoot;
    public void Localize(Func<string,string> l)=>_l=l;
    public void Open() {
        if(_disposed)return;
        if(!MusicAceCatalog.IsAce(view.Generation.Variation))throw new InvalidOperationException("Music.AceIdeas.Unsupported");
        if(_window is not null){_window.Activate();return;}
        _window=new(this,_l){Owner=Window.GetWindow(view)};_window.Closed+=(_,_)=>_window=null;_window.Show();
    }
    public void Update(MusicAceIdeaWorkspace state) {
        if(view.Projects.Busy&&!Running)throw new InvalidOperationException("Music.Generation.OtherOperation");
        state.Validate();view.Generation.AceIdeas=state;view.Projects.SaveScores(view.Projects.Current.Id);Changed?.Invoke();
    }
    public async Task PlanAsync(int count) {
        if(Running)return;
        if(ApplicationBackgroundOperations.Current is {HasPending:true,State.Kind:MusicAceIdeaWork.Kind} pending){await pending.ResumeAsync(ApplicationBackgroundOperations.ExitToken);return;}
        if(ApplicationBackgroundOperations.Current?.BlocksNewWork==true)throw new InvalidOperationException("Music.Generation.OtherOperation");
        if(!MusicAceCatalog.IsAce(view.Generation.Variation))throw new InvalidDataException("Music.AceIdeas.Unsupported");
        if(State.Ideas.Length+count>128)throw new InvalidDataException("Music.AceIdeas.HistoryFull");
        if(!view.Editor.TryGetGenerationText(out var lyrics))return;
        if(view.Generation.AudioTask.Enabled||view.Generation.Melody.Enabled)throw new InvalidDataException("Music.AceIdeas.Conflict");
        var snapshot=view.Projects.Capture() with {Lyrics=lyrics,AceIdeas=new(),Scores=new()};
        var work=new MusicAceIdeaWorkRequest(Guid.NewGuid().ToString("N"),view.Projects.Current.Id,_modelsRoot,view.Wishes.RequestStyle,snapshot,
            Enumerable.Range(0,Math.Clamp(count,1,8)).Select(i=>new MusicAceIdeaCandidate(snapshot.Expert.Integer("seed")<0?
                RandomNumberGenerator.GetInt32(1,int.MaxValue):(int)(((long)snapshot.Expert.Integer("seed")+i)%int.MaxValue))).ToArray());
        MusicAceIdeaStore.Default.Save(work);Update(State with {PendingWorkId=work.Id,Errors=[]});
        _run=RunAsync(work,null,ApplicationBackgroundOperations.ExitToken);await _run;
    }
    public Task ResumeAsync(BackgroundOperationState state,CancellationToken token) {
        var work=MusicAceIdeaStore.Default.Load(state.Input.GetProperty("Id").GetString()!);
        if(view.Projects.Current.Id!=work.ProjectId)view.Projects.Open(work.ProjectId);
        _run=RunAsync(work,state,token);return _run;
    }
    public async Task PauseAsync() {if(ApplicationBackgroundOperations.Current is {State.Kind:MusicAceIdeaWork.Kind} pending)await pending.PauseAsync();}
    private async Task RunAsync(MusicAceIdeaWorkRequest work,BackgroundOperationState? restored,CancellationToken token) {
        view.Projects.SetBusy(true);LastError=null;Changed?.Invoke();var worker=new MusicAceWorker();
        worker.Log+=line=>view.Dispatcher.Invoke(()=>view.Status.AppendLog(line));
        try {
            await MusicAceIdeaWork.RunAsync(work,worker,changed=>view.Dispatcher.Invoke(()=>Accept(changed)),token,restored);
            Accept(MusicAceIdeaStore.Default.Load(work.Id));
        }catch(OperationCanceledException){}
        catch(Exception error){Error(error);}
        finally{view.Projects.SetBusy(false);Changed?.Invoke();}
    }
    private void Accept(MusicAceIdeaWorkRequest work) {
        if(view.Projects.Current.Id!=work.ProjectId)throw new InvalidDataException("Music.AceIdeas.InputChanged");
        var state=State;var all=state.Ideas.Concat(work.Candidates.Select(c=>c.Idea).OfType<MusicAceIdea>()).DistinctBy(i=>i.Id).ToArray();
        LastError=work.Candidates.LastOrDefault(c=>c.Error is not null)?.Error;
        view.Generation.AceIdeas=state with {Ideas=all,Errors=work.Candidates.Select(c=>c.Error).OfType<string>().ToArray(),PendingWorkId=work.Candidates.All(c=>c.Idea is not null||c.Error is not null)?null:work.Id};
        view.Projects.SaveScores(work.ProjectId);Changed?.Invoke();
    }
    public async Task FromSongAsync() {
        if(Running||view.Projects.Busy)throw new InvalidOperationException("Music.Generation.OtherOperation");
        if(State.Ideas.Length>=128)throw new InvalidDataException("Music.AceIdeas.HistoryFull");
        var track=view.Player.CurrentTrack;if(track?.JobId is null)throw new InvalidDataException("Music.AceIdeas.NoSong");
        var idea=MusicAceIdeaPipeline.FromSong(jobs.Load(track.JobId),track.Variant);
        if(MessageBox.Show(Window.GetWindow(view),_l("Music.AceIdeas.RestoreConfirm"),_l("Music.AceIdeas.Title"),MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
        Restore(idea,State with {Ideas=[..State.Ideas,idea],Selected=[idea.Id],Retake=true});await Task.CompletedTask;
    }
    public void RestoreSelected() {
        var idea=State.Selected.Select(id=>State.Ideas.Single(i=>i.Id==id)).FirstOrDefault()??throw new InvalidDataException("Music.AceIdeas.NeedSelection");
        if(MessageBox.Show(Window.GetWindow(view),_l("Music.AceIdeas.RestoreConfirm"),_l("Music.AceIdeas.Title"),MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes)Restore(idea,State);
    }
    private void Restore(MusicAceIdea idea,MusicAceIdeaWorkspace state) {
        var current=view.Projects.Capture();
        view.Projects.ApplyExample(idea.Input with {AceIdeas=state,Title=current.Title,Artist=current.Artist,Comment=current.Comment,
            Output=current.Output,SeparateTracks=current.SeparateTracks,Variants=current.Variants,Scores=current.Scores,ModelSettings=current.ModelSettings});
        view.Projects.SaveScores(view.Projects.Current.Id);Changed?.Invoke();
    }
    public void Apply() {
        var state=State with {Enabled=true};CheckCurrent(state);Update(state);
    }
    private void CheckCurrent(MusicAceIdeaWorkspace state) {
        if(state.Selected.Length==0)throw new InvalidDataException("Music.AceIdeas.NeedSelection");
        if(!view.Editor.TryGetGenerationText(out var lyrics))throw new InvalidDataException("Music.AceIdeas.Invalid");
        var options=view.Generation.Options;
        MusicAceIdeaPipeline.Validate(state,view.Generation.Variation,options.AudioTask,options.Melody);
        var binding=MusicAceIdea.Bind(view.Wishes.RequestStyle,lyrics,view.Generation.ExpertSettings,
            MusicLoraCatalog.Capture(view.Lora.Selection,MusicAceCatalog.Variation),options.DurationSeconds);
        if(state.Selected.Any(id=>state.Ideas.Single(i=>i.Id==id).InputHash!=binding))throw new InvalidDataException("Music.AceIdeas.InputChanged");
    }
    public async Task RenderAsync(bool preview) {
        if(Running)throw new InvalidOperationException("Music.AceIdeas.Wait");
        if(ApplicationBackgroundOperations.Current?.BlocksNewWork==true)throw new InvalidOperationException("Music.Generation.OtherOperation");
        var state=State with {Enabled=true};CheckCurrent(state);
        var count=preview?1:state.Selected.Length*view.Generation.Options.Variants;
        if(count>8)throw new InvalidDataException("Music.Score.TooManySongs");
        if(MessageBox.Show(Window.GetWindow(view),string.Format(_l(preview?"Music.AceIdeas.PreviewConfirm":"Music.AceIdeas.RenderConfirm"),count),
            _l("Music.AceIdeas.Title"),MessageBoxButton.OKCancel,MessageBoxImage.Question)!=MessageBoxResult.OK)return;
        Update(state);var project=view.Projects.Current.Id;_window?.Close();await view.Session.EnsureRuntimeReadyAsync();
        if(view.Projects.Current.Id!=project||!MusicAceCatalog.IsAce(view.Generation.Variation))throw new InvalidDataException("Music.AceIdeas.InputChanged");
        await view.Session.StartAceIdeasAsync(preview);
    }
    public void Error(Exception error) {
        LastError=error.Message;Changed?.Invoke();
        MessageBox.Show(Window.GetWindow(view),error.Message.StartsWith("Music.")?_l(error.Message):_l("Music.AceIdeas.Invalid")+"\n"+error.Message,
            _l("Music.AceIdeas.Title"),MessageBoxButton.OK,MessageBoxImage.Warning);
    }
    public void Dispose(){_disposed=true;view.Generation.OptionsChanged-=OptionsChanged;_window?.Close();}
}
