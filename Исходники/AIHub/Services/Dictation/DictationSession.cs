namespace AIHub.Services.Dictation;

/// <summary>One resident recognizer and one capture. Final chunks survive preview coalescing.</summary>
public sealed class DictationSession : IAsyncDisposable
{
    private readonly DictationRecognizerHost _host=new();
    private readonly LinkedList<DictationChunk> _queue=[];
    private readonly object _gate=new();
    private readonly SemaphoreSlim _signal=new(0);
    private readonly CancellationTokenSource _lifetime=new();
    private readonly Task _pump;
    private DictationAudioCapture? _audio;
    private TaskCompletionSource _drained=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _pending;
    private Task _stopTask=Task.CompletedTask;
    private bool _disposed;
    public bool Recording=>_audio is not null;
    public bool Processing=>Volatile.Read(ref _pending)>0||!_stopTask.IsCompleted;
    public bool Ready=>_host.Ready;
    public double Level=>_audio?.Level??0;
    public Func<DictationChunk,string,Task>? TextReady { get; set; }
    public event Action<Exception>? Failed;
    public event Action? Changed;
    public DictationSession(){_drained.TrySetResult();_pump=Task.Run(Pump);}
    public Task PrepareAsync(string[] files,int threads,CancellationToken token)=>_host.LoadAsync(files,threads,token);
    public void Start(int device)
    {
        if(_disposed||Recording||Processing||!Ready)throw new InvalidOperationException("Dictation is not ready.");
        var audio=new DictationAudioCapture(Enqueue);audio.Failed+=OnFailure;
        try{audio.Start(device);_audio=audio;Changed?.Invoke();}
        catch{audio.Dispose();throw;}
    }
    private void Enqueue(DictationChunk chunk)
    {
        lock(_gate) {
            // Bound backlog memory on a CPU that cannot keep up; session duration itself has no cap.
            if(_queue.Count>=32)throw new InvalidOperationException("Dictation.Overloaded");
            if(_pending==0)_drained=new(TaskCreationOptions.RunContinuationsAsynchronously);
            for(var n=_queue.First;n is not null;){var next=n.Next;if(n.Value.Segment==chunk.Segment&&!n.Value.Final){_queue.Remove(n);_pending--;}n=next;}
            _queue.AddLast(chunk);_pending++;
        }
        _signal.Release();Changed?.Invoke();
    }
    private async Task Pump()
    {
        while(!_lifetime.IsCancellationRequested) {
            try {
                await _signal.WaitAsync(_lifetime.Token);DictationChunk? chunk;
                lock(_gate){chunk=_queue.First?.Value;if(chunk is not null)_queue.RemoveFirst();}
                if(chunk is null)continue;
                try {
                    var text=await _host.DecodeAsync(chunk.Pcm,_lifetime.Token);
                    if(TextReady is {} apply)await apply(chunk,text);
                }
                catch(OperationCanceledException) when(_lifetime.IsCancellationRequested){break;}
                catch(Exception error){OnFailure(error);lock(_gate){_pending-=_queue.Count;_queue.Clear();}}
                finally {lock(_gate){if(--_pending==0)_drained.TrySetResult();}Changed?.Invoke();}
            }
            catch(OperationCanceledException) when(_lifetime.IsCancellationRequested){break;}
        }
    }
    private void OnFailure(Exception error){Failed?.Invoke(error);}
    public Task StopAsync()
    {
        if(!_stopTask.IsCompleted)return _stopTask;
        var audio=_audio;_audio=null;
        _stopTask=StopCore();return _stopTask;
        async Task StopCore() {
            if(audio is not null){try{await audio.StopAsync();}finally{audio.Dispose();}}
            Changed?.Invoke();await _drained.Task.WaitAsync(TimeSpan.FromMinutes(2),_lifetime.Token);Changed?.Invoke();
        }
    }
    public async Task UnloadAsync(){await StopAsync();await _host.DisposeAsync();Changed?.Invoke();}
    public async ValueTask DisposeAsync()
    {
        if(_disposed)return;_disposed=true;
        // Exit discards undelivered IPC; no capture is restarted on the next launch.
        var audio=_audio;_audio=null;
        if(audio is not null){audio.Failed-=OnFailure;try{await audio.StopAsync();}finally{audio.Dispose();}}
        TextReady=null;_lifetime.Cancel();await _host.DisposeAsync();await _pump;
        lock(_gate){_queue.Clear();_pending=0;_drained.TrySetResult();}
        _lifetime.Dispose();_signal.Dispose();
    }
}
