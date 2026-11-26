using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AIHub.Services.Dictation;

public sealed class DictationRecognizerHost(string? operationComponent = null) : IAsyncDisposable
{
    private Process? _process;
    private Task? _reader,_stderr;
    private readonly SemaphoreSlim _serial=new(1,1);
    private readonly ConcurrentDictionary<long,TaskCompletionSource<string>> _pending=[];
    private TaskCompletionSource? _ready;
    private long _sequence;
    public bool Ready => _process is {HasExited:false}&&_ready?.Task.IsCompletedSuccessfully==true;

    public async Task LoadAsync(string[] files,int threads,CancellationToken token,string? testAuditStore=null)
    {
        await DisposeAsync();
        var info=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"AIHub.exe")) {
            UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,
            StandardInputEncoding=new UTF8Encoding(false),StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8,
            WorkingDirectory=AppContext.BaseDirectory
        };
        info.ArgumentList.Add("--dictation-worker");
        var process=await ApplicationInspector.StartWorkerAsync(info,operationComponent ?? "VoiceInput.Parakeet",token,
            backend:"ONNX Runtime", device:"cpu");_process=process;
        var ready=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);_ready=ready;
        _reader=Read(process,ready);
        _stderr=Task.Run(async()=>{while(await process.StandardError.ReadLineAsync() is {} _) { /* Native diagnostics may contain model text: do not persist them. */ }});
        try {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new DictationWorkerInit(files,threads,ComponentAudit.Enabled,testAuditStore)));
            await process.StandardInput.FlushAsync(token);
            await ready.Task.WaitAsync(TimeSpan.FromMinutes(4),token);
            ApplicationInspector.WorkerFinished(process);
        }
        catch(Exception error) {
            await ApplicationInspector.WorkerFailedAsync(process,error,token);
            await DisposeAsync();throw;
        }
    }
    private async Task Read(Process process,TaskCompletionSource ready)
    {
        try {
            while(await process.StandardOutput.ReadLineAsync() is {} line) {
                using var doc=JsonDocument.Parse(line);var row=doc.RootElement;
                switch(row.GetProperty("type").GetString()) {
                    case "ready":ready.TrySetResult();OwnedProcessRegistry.Log("dictation_ready",operationComponent ?? "VoiceInput",process.Id);break;
                    case "result":
                        if(_pending.TryRemove(row.GetProperty("id").GetInt64(),out var result))result.TrySetResult(row.GetProperty("text").GetString()??"");
                        OwnedProcessRegistry.Log("dictation_decode",operationComponent ?? "VoiceInput",process.Id,"elapsedMs="+row.GetProperty("decodeMs").GetDouble().ToString("F1",System.Globalization.CultureInfo.InvariantCulture));break;
                    case "error":throw new InvalidDataException(row.GetProperty("message").GetString());
                }
            }
            if(_process==process)throw new IOException("Dictation worker exited unexpectedly.");
        }
        catch(Exception error) {
            ready.TrySetException(error);
            if(_process==process)foreach(var item in _pending.Values)item.TrySetException(error);
        }
    }
    public async Task<string> DecodeAsync(byte[] pcm,CancellationToken token)
    {
        await _serial.WaitAsync(token);var id=Interlocked.Increment(ref _sequence);
        GenerationInspectorAttempt? inspection = null;
        try {
            if(!Ready)throw new IOException("Dictation worker is not ready.");
            inspection=await ApplicationInspector.BeginAttemptAsync(operationComponent ?? "VoiceInput.Parakeet",
                "ONNX Runtime","cpu",token,stage:"generating");
            inspection.LinkRequest(id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            inspection.Attach(_process!);
            var result=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);_pending[id]=result;
            await _process!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new DictationAudioRequest(id,Convert.ToBase64String(pcm))));
            await _process.StandardInput.FlushAsync(token);
            var text=await result.Task.WaitAsync(TimeSpan.FromMinutes(2),token);
            inspection.Succeed();return text;
        }
        catch(Exception error) {
            if(inspection is not null)
            {
                var outcome=_process is { HasExited:true } process ? GenerationFailureClassifier.FromWorker(process.ExitCode,null,"cpu")
                    : Lopata.Inspector.InspectorOutcome.Unknown;
                await inspection.FailAsync(error,token,token.IsCancellationRequested || outcome is Lopata.Inspector.InspectorOutcome.Unknown
                    or Lopata.Inspector.InspectorOutcome.Failure ? null : outcome);
            }
            throw;
        }
        finally{_pending.TryRemove(id,out _);_serial.Release();}
    }
    public async ValueTask DisposeAsync()
    {
        var process=_process;_process=null;if(process is null)return;
        foreach(var item in _pending.Values)item.TrySetCanceled();
        try {
            if(!process.HasExited) {
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new DictationAudioRequest(0,Exit:true)));
                await process.StandardInput.FlushAsync();await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            }
        }
        catch(Exception error) when(error is IOException or InvalidOperationException or TimeoutException) {
            if(!process.HasExited)await OwnedProcessRegistry.Shared.StopAndWaitAsync(process.Id);
        }
        if(_reader is {} reader)await reader;if(_stderr is {} stderr)await stderr;
        process.Dispose();
    }
}
