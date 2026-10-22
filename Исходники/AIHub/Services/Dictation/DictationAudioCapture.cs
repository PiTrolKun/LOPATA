using System.Diagnostics;
using System.IO;
using NAudio.Wave;

namespace AIHub.Services.Dictation;

public sealed record DictationChunk(int Segment,bool Final,byte[] Pcm,long CapturedTicks);

/// <summary>Bounded PCM/VAD windows, no audio files. Segmentation matches the accepted stand.</summary>
public sealed class DictationAudioCapture(Action<DictationChunk> submit) : IDisposable
{
    private readonly object _gate=new();
    private readonly MemoryStream _phrase=new();
    private readonly TaskCompletionSource _stopped=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WaveInEvent? _capture;
    private bool _speech;
    private double _silence;
    private int _segment;
    private long _lastPreview=Stopwatch.GetTimestamp();
    public double Level { get; private set; }
    public event Action<Exception>? Failed;
    public void Start(int device)
    {
        _capture=new(){DeviceNumber=device,WaveFormat=new WaveFormat(16000,16,1),BufferMilliseconds=50};
        _capture.DataAvailable+=(_,e)=>{try{Push(e.Buffer.AsSpan(0,e.BytesRecorded));}catch(Exception error){Failed?.Invoke(error);}};
        _capture.RecordingStopped+=(_,e)=>{if(e.Exception is {} error)Failed?.Invoke(error);_stopped.TrySetResult();};
        _capture.StartRecording();
    }
    public void Push(ReadOnlySpan<byte> bytes)
    {
        lock(_gate) {
            double energy=0;for(var i=0;i+1<bytes.Length;i+=2){var sample=BitConverter.ToInt16(bytes[i..])/32768d;energy+=sample*sample;}
            Level=Math.Sqrt(energy/Math.Max(1,bytes.Length/2));_phrase.Write(bytes);
            if(Level>=0.008){_speech=true;_silence=0;}else _silence+=bytes.Length/32000d;
            if(!_speech) {
                if(_phrase.Length>9600){var tail=_phrase.ToArray()[^8000..];_phrase.SetLength(0);_phrase.Write(tail);}return;
            }
            if((_silence>=0.7&&_phrase.Length>=16000)||_phrase.Length>=12*32000) {
                Snapshot(true);_segment++;_phrase.SetLength(0);_speech=false;_silence=0;
            }
            else if(_phrase.Length>=19200&&Stopwatch.GetElapsedTime(_lastPreview).TotalSeconds>=0.5) {
                Snapshot(false);_lastPreview=Stopwatch.GetTimestamp();
            }
        }
    }
    private void Snapshot(bool final)=>submit(new(_segment,final,_phrase.ToArray(),Stopwatch.GetTimestamp()));
    public async Task StopAsync()
    {
        if(_capture is not null){_capture.StopRecording();await _stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));}
        Finish();
    }
    public void Finish(){lock(_gate){if(_speech&&_phrase.Length>0)Snapshot(true);_phrase.SetLength(0);_speech=false;Level=0;}}
    public void Dispose(){_capture?.Dispose();_phrase.Dispose();}
}
