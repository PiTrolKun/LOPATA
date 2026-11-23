using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.NeuroConstructor;

public static partial class NeuroService
{
    internal const string TrainingKind = "neuroconstructor.train";
    private sealed record RunPointer(string ProjectId,string RunId,bool AutoBuild=false);
    private static readonly ConcurrentDictionary<string,CancellationTokenSource> Active = new();
    private static string PointerFile(string project,string name) => Path.Combine(NeuroFiles.Project(project),name+".json");
    private static string? Pointer(string project,string name) => File.Exists(PointerFile(project,name))
        ? NeuroFiles.Read<string>(PointerFile(project,name)) : null;
    private static NeuroStatus? ReadProgress(string? file)
    {
        if (file==null || !File.Exists(file)) return null;
        try { return NeuroFiles.Read<NeuroStatus>(file); }
        catch (Exception error) when (error is JsonException or InvalidDataException) { return null; }
    }
    private static bool PublishedReport(string directory,string composition)
    {
        var path=Path.Combine(directory,"report.json");
        if (!File.Exists(path) || !File.Exists(Path.Combine(directory,"learned",composition+".pt"))) return false;
        try
        {
            var data=NeuroFiles.Read<JsonElement>(path);
            return data.ValueKind==JsonValueKind.Object && data.TryGetProperty("eligible",out var ok) &&
                ok.ValueKind is JsonValueKind.True or JsonValueKind.False;
        }
        catch (JsonException) { return false; }
    }
    private static BackgroundOperationState? Pending(string project) =>
        ApplicationBackgroundOperations.Current?.FindOperations(TrainingKind,project).FirstOrDefault(x =>
            x.Phase is not (BackgroundOperationPhase.Canceled or BackgroundOperationPhase.Failed or BackgroundOperationPhase.Completed));
    public static NeuroStatus Status(NeuroProject p)
    {
        var id = Pointer(p.Id,"current");
        var state = Pending(p.Id);
        var path = id == null ? null : Path.Combine(NeuroFiles.Run(p.Id,id),"status.json");
        var result = (p.Mode == "auto" && id != null ? ReadProgress(Path.Combine(NeuroFiles.Run(p.Id,id),"auto-status.json")) : null)
            ?? ReadProgress(path) ?? new NeuroStatus(state == null ? "ready" : "running","",0,0,0,null,"");
        if (p.Mode == "auto" && result.Status == "completed" && id != null &&
            !File.Exists(Path.Combine(NeuroFiles.Run(p.Id,id),"auto-output.json")))
            result = result with { Status="running" };
        return state?.Phase switch {
            BackgroundOperationPhase.Pausing => result with {Status="pausing"},
            BackgroundOperationPhase.Paused => result with {Status="paused"},
            BackgroundOperationPhase.Waiting or BackgroundOperationPhase.Countdown => result with {Status=state.UserPaused ? "paused" : "waiting",Message=state.Detail ?? result.Message},
            _ => result };
    }
    public static string ReportPath(NeuroProject p) => Pointer(p.Id,p.Mode == "auto" ? "current" : "finished") is { } id
        ? Path.Combine(NeuroFiles.Run(p.Id,id),"report.json") : Path.Combine(NeuroFiles.Project(p.Id),"report.json");
    public static bool ReportMatches(NeuroProject p)
    {
        if (Pointer(p.Id,"finished") is not { } id) return false;
        var previous = NeuroFiles.Read<NeuroProject>(Path.Combine(NeuroFiles.Run(p.Id,id),"settings.json"));
        return JsonSerializer.Serialize(p,NeuroFiles.Json) == JsonSerializer.Serialize(previous,NeuroFiles.Json);
    }
    public static Task TrainAsync(NeuroProject p,CancellationToken token=default) => StartRunAsync(p,false,token);
    public static Task EvaluateAsync(NeuroProject p,CancellationToken token=default) => StartRunAsync(p,true,token);
    private sealed record Evaluation(bool EvaluateOnly,string? ReusableWeights,string? PreviousPairWeights);
    private static bool SameSource(NeuroProject a,NeuroProject b) =>
        a.Source==b.Source && a.ParentPackage==b.ParentPackage;
    private static string? CompatibleWeights(NeuroProject p,bool previousPair=false)
    {
        if (Pointer(p.Id,previousPair ? "pair" : "finished") is not { } id) return null;
        var dir=NeuroFiles.Run(p.Id,id);
        var old=NeuroFiles.Read<NeuroProject>(Path.Combine(dir,"settings.json"));
        if (!SameSource(p,old)) return null;
        if (previousPair ? p.Composition!="trio" || old.Composition!="pair" :
            old.Composition!=p.Composition || old.Connection!=p.Connection) return null;
        return Path.Combine(dir,"learned");
    }
    public static bool CanEvaluate(NeuroProject p) => CompatibleWeights(p)!=null || HasReadyConnection(p);
    private static Task StartRunAsync(NeuroProject p,bool evaluateOnly,CancellationToken token,string? autoDestination=null)
    {
        lock (Gate)
        {
            if (Pending(p.Id) != null || Active.ContainsKey(p.Id)) throw new InvalidOperationException("Neuro.RunPending");
            Save(p);
            var pointer = new RunPointer(p.Id,Guid.NewGuid().ToString("N"),autoDestination != null);
            var directory = NeuroFiles.Run(p.Id,pointer.RunId);
            var weights = evaluateOnly && (!pointer.AutoBuild || NeuroProfiles.Full(p.Composition)) ? CompatibleWeights(p) : null;
            if (evaluateOnly && weights==null && !HasReadyConnection(p)) throw new InvalidOperationException("Neuro.TrainingRequired");
            NeuroFiles.Write(Path.Combine(directory,"settings.json"),p);
            var previous = pointer.AutoBuild ? null : CompatibleWeights(p,previousPair:true);
            NeuroFiles.Write(Path.Combine(directory,"evaluation.json"),new Evaluation(evaluateOnly,weights,previous));
            if (autoDestination != null) SaveAutoExport(p,pointer.RunId,autoDestination);
            return RunAsync(pointer,null,token);
        }
    }
    internal static Task RestoreAsync(BackgroundOperationState state,CancellationToken token)
    {
        var pointer = state.Input.Deserialize<RunPointer>(NeuroFiles.Json) ?? throw new InvalidDataException("Neuro.ProjectId");
        if (pointer.ProjectId != state.Project) throw new InvalidDataException("Neuro.ProjectId");
        return RunAsync(pointer,state,token);
    }
    private static async Task RunAsync(RunPointer pointer,BackgroundOperationState? restored,CancellationToken token)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (!Active.TryAdd(pointer.ProjectId,stop)) throw new InvalidOperationException("Neuro.RunPending");
        try
        {
            await ApplicationBackgroundOperations.RunAsync(TrainingKind,"Нейроконструктор",pointer.ProjectId,pointer,
                ct => pointer.AutoBuild ? ExecuteAutoBuildAsync(pointer,ct) : ExecuteTrainingAsync(pointer,ct),stop.Token,restored);
        }
        finally { Active.TryRemove(pointer.ProjectId,out _); }
    }
    private static async Task<bool> ExecuteTrainingAsync(RunPointer pointer,CancellationToken token)
    {
        var dir = NeuroFiles.Run(pointer.ProjectId,pointer.RunId);
        var p = NeuroFiles.Read<NeuroProject>(Path.Combine(dir,"settings.json"));
        NeuroFiles.Write(PointerFile(p.Id,"current"),pointer.RunId);
        var statusFile = Path.Combine(dir,"status.json");
        var report = Path.Combine(dir,"report.json");
        var pause = Path.Combine(dir,"pause.request");
        var cancel = Path.Combine(dir,"cancel.request");
        if (File.Exists(cancel)) throw new BackgroundOperationFailedException("Neuro.Canceled");
        try
        {
            await ReattachAsync(dir,pointer.RunId,token);
            var status = ReadProgress(statusFile);
            if (status?.Status != "completed" || !PublishedReport(dir,p.Composition))
            {
                File.Delete(pause);
                await NeuroRuntime.ConfirmModelsAsync(p.Composition,token);
                await NeuroRuntime.AuditAsync(token);
                await NeuroRuntime.AuditModelsAsync(p.Source,p.Composition,ParentKind(p),token);
                var python = await NeuroPlacement.ResolveAsync(p,token);
                var input = Path.Combine(dir,"run.json");
                if (!File.Exists(input))
                {
                    var evaluation = NeuroFiles.Read<Evaluation>(Path.Combine(dir,"evaluation.json"));
                    NeuroFiles.Write(input,NeuroPlacement.Options(p,new { pointer.RunId,p.Source,p.ParentPackage,p.Composition,p.Cycles,p.Attempts,
                        Device=python.Device,p.Connection,p.AudioPolicy,p.Channel,p.MemoryMiB,p.Examples,includeReference=p.UseReferenceExamples,
                        evaluation.EvaluateOnly,evaluation.ReusableWeights,evaluation.PreviousPairWeights }));
                }
                try
                {
                    await NeuroRuntime.ExecuteAsync(python,"training.py",["--project",dir],token,
                        () => File.WriteAllText(pause,pointer.RunId));
                }
                catch (NeuroWorkerAlreadyRunningException) { await ReattachAsync(dir,pointer.RunId,token); }
                token.ThrowIfCancellationRequested();
                status = NeuroFiles.Read<NeuroStatus>(statusFile);
                if (status.Status != "completed" || !File.Exists(report)) throw new InvalidDataException(status.Message ?? "Neuro.RunFailed");
            }
            NeuroFiles.Write(PointerFile(p.Id,"finished"),pointer.RunId);
            if (NeuroFiles.Read<JsonElement>(report).GetProperty("eligible").GetBoolean())
            {
                NeuroFiles.Write(PointerFile(p.Id,"accepted"),pointer.RunId);
                if (p.Composition=="pair") NeuroFiles.Write(PointerFile(p.Id,"pair"),pointer.RunId);
            }
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            NeuroFiles.Write(statusFile,new NeuroStatus("failed","",0,0,0,null,ex.Message));
            throw new BackgroundOperationFailedException(ex.Message,ex);
        }
    }
    // The process lock belongs to this immutable run folder. A PID can be recycled.
    private static async Task ReattachAsync(string directory,string runId,CancellationToken token)
    {
        var path = Path.Combine(directory,"worker.lock");
        if (!File.Exists(path)) return;
        using var stream = new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.ReadWrite);
        static bool Locked(FileStream file)
        {
            try { file.Lock(0,1); file.Unlock(0,1); return false; }
            catch (IOException ex) when ((ex.HResult & 0xffff) == 33) { return true; }
        }
        if (!Locked(stream)) return;
        var input = NeuroFiles.Read<JsonElement>(Path.Combine(directory,"run.json"));
        if (input.GetProperty("runId").GetString() != runId) throw new InvalidDataException("Neuro.ProjectId");
        try { while (Locked(stream)) await Task.Delay(250,token); }
        catch (OperationCanceledException)
        {
            File.WriteAllText(Path.Combine(directory,"pause.request"),runId);
            while (Locked(stream)) await Task.Delay(250);
            throw;
        }
    }
    public static Task PauseAsync(NeuroProject p) => Pending(p.Id) is { } state
        ? ApplicationBackgroundOperations.Current!.PauseAsync(state.Id) : Task.CompletedTask;
    public static Task ResumeAsync(NeuroProject p) => Pending(p.Id) is { } state
        ? ApplicationBackgroundOperations.Current!.ResumeAsync(state.Id,CancellationToken.None)
        : throw new InvalidOperationException("Neuro.NoPausedRun");
    public static async Task CancelAsync(NeuroProject p)
    {
        var state = Pending(p.Id);
        var id = state?.Input.Deserialize<RunPointer>(NeuroFiles.Json)?.RunId ?? Pointer(p.Id,"current");
        if (id == null || state == null && !Active.ContainsKey(p.Id)) return;
        var dir = NeuroFiles.Run(p.Id,id);
        File.WriteAllText(Path.Combine(dir,"cancel.request"),id);
        if (Active.TryGetValue(p.Id,out var cts)) await cts.CancelAsync();
        if (ApplicationBackgroundOperations.Current is { } controller && state != null)
        {
            await controller.WaitForIdleAsync(state.Id);
            controller.DiscardPending(state.Id);
        }
        var statusFile=p.Mode=="auto" ? "auto-status.json" : "status.json";
        NeuroFiles.Write(Path.Combine(dir,statusFile),new NeuroStatus("canceled","",0,0,0,null,""));
    }
}
