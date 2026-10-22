using System.Diagnostics;
using System.IO;
using NAudio.Wave;

namespace VoiceInputStand;

public sealed record AudioRequest(string Id,int Segment,bool Final,string Path,double Seconds,double StartSeconds,long CapturedTicks);

public sealed class AudioSession : IDisposable
{
    public static string NormalizeFile(string input,string directory)
    {
        var output=Path.Combine(directory,"input-16k-"+Guid.NewGuid().ToString("N")[..8]+".wav");
        using var reader=new WaveFileReader(input);
        using var converter=new MediaFoundationResampler(reader,new WaveFormat(16000,16,1)){ResamplerQuality=60};
        WaveFileWriter.CreateWaveFile(output,converter);
        return output;
    }
    private readonly object _gate=new();
    private readonly MemoryStream _phrase=new();
    private readonly StandLog _log;
    private readonly Action<AudioRequest> _submit;
    private readonly double _interval;
    private WaveInEvent? _capture;
    private WaveFileWriter? _raw;
    private readonly TaskCompletionSource _stopped=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _totalBytes;
    private double _silence;
    private bool _speech;
    private int _segment=1;
    private long _lastPreview;
    public string RecordingPath {get;}
    public event Action<double>? Level;
    public event Action<Exception>? Failed;
    public AudioSession(StandLog log,Action<AudioRequest> submit,double interval)
    {
        _log=log;_submit=submit;_interval=interval;
        _lastPreview=Stopwatch.GetTimestamp();
        RecordingPath=Path.Combine(log.DirectoryPath,"recording-"+Guid.NewGuid().ToString("N")[..8]+".wav");
    }
    public void Start(int device)
    {
        _raw=new(RecordingPath,new WaveFormat(16000,16,1));
        _capture=new(){DeviceNumber=device,WaveFormat=new WaveFormat(16000,16,1),BufferMilliseconds=50};
        _capture.DataAvailable+=(_,e)=>{try{Push(e.Buffer,e.BytesRecorded);}catch(Exception error){Failed?.Invoke(error);}};
        _capture.RecordingStopped+=(_,e)=>{if(e.Exception is {} error)Failed?.Invoke(error);_stopped.TrySetResult();};
        _log.Write("microphone_start",new{device,sampleRate=16000,channels=1,bits=16,interval=_interval,path=RecordingPath});
        _capture.StartRecording();
    }
    public void Push(byte[] bytes,int count)
    {
        lock(_gate)
        {
            _raw?.Write(bytes,0,count);
            _totalBytes+=count;
            double energy=0,peak=0;var clipped=0;
            for(var i=0;i+1<count;i+=2){var sample=BitConverter.ToInt16(bytes,i);var value=sample/32768d;energy+=value*value;peak=Math.Max(peak,Math.Abs(value));if(Math.Abs((int)sample)>=32760)clipped++;}
            var rms=Math.Sqrt(energy/Math.Max(1,count/2));
            Level?.Invoke(rms);
            _log.Write("audio_packet",new{bytes=count,totalSamples=_totalBytes/2,rms,peak,clippedSamples=clipped});
            _phrase.Write(bytes,0,count);
            if(rms>=0.008){_speech=true;_silence=0;}else _silence+=count/32000d;
            if(!_speech)
            {
                if(_phrase.Length>9600){var tail=_phrase.ToArray()[^8000..];_phrase.SetLength(0);_phrase.Write(tail);}
                return;
            }
            if((_silence>=0.7 && _phrase.Length>=16000)||_phrase.Length>=12*32000)
            {Snapshot(true);_segment++;_phrase.SetLength(0);_speech=false;_silence=0;}
            else if(_phrase.Length>=19200 && Stopwatch.GetElapsedTime(_lastPreview).TotalSeconds>=_interval)
            {Snapshot(false);_lastPreview=Stopwatch.GetTimestamp();}
        }
    }
    private void Snapshot(bool final)
    {
        var ident=Guid.NewGuid().ToString("N");
        var path=Path.Combine(_log.DirectoryPath,$"chunk-{_segment:D4}-{(final?"final":"preview")}-{ident[..8]}.wav");
        using(var writer=new WaveFileWriter(path,new WaveFormat(16000,16,1))) _phrase.WriteTo(writer);
        var request=new AudioRequest(ident,_segment,final,path,_phrase.Length/32000d,(_totalBytes-_phrase.Length)/32000d,Stopwatch.GetTimestamp());
        _log.Write("snapshot",request);_submit(request);
    }
    public void FinishFile(){lock(_gate){if(_speech && _phrase.Length>0)Snapshot(true);_phrase.SetLength(0);_speech=false;}}
    public async Task StopAsync()
    {
        if(_capture is not null){_capture.StopRecording();await _stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));}
        FinishFile();
        lock(_gate){_raw?.Dispose();_raw=null;}
        _log.Write("microphone_stop",new{samples=_totalBytes/2,seconds=_totalBytes/32000d,path=RecordingPath});
    }
    public void Dispose(){_capture?.Dispose();_raw?.Dispose();_phrase.Dispose();}
}
