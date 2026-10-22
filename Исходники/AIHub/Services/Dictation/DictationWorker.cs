using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AIHub.Services.Dictation;

public sealed record DictationWorkerInit(string[] Files,int Threads,bool AuditEnabled,string? TestAuditStore=null);
public sealed record DictationAudioRequest(long Id,string? Pcm=null,bool Exit=false);

/// <summary>Private resident CPU worker; no UI, microphone, audio files or transcript logs.</summary>
public static class DictationWorker
{
    public static async Task<int> RunAsync()
    {
        using var input=new StreamReader(Console.OpenStandardInput(),Encoding.UTF8);
        await using var output=new StreamWriter(Console.OpenStandardOutput(),new UTF8Encoding(false)){AutoFlush=true};
        async Task Send(object value)=>await output.WriteLineAsync(JsonSerializer.Serialize(value));
        try {
            var raw=await input.ReadLineAsync()??throw new EndOfStreamException();
            var init=JsonSerializer.Deserialize<DictationWorkerInit>(raw)??throw new InvalidDataException();
            if(init.Threads is <1 or >16)throw new InvalidDataException("Invalid thread count.");
            ComponentAudit.Configure(init.AuditEnabled);
            using var scope=init.TestAuditStore is {} test ? ComponentAudit.UseTestStore(test,init.AuditEnabled) : null;
            using var operation=ComponentAudit.CaptureOperation();
            await DictationComponents.AuditAsync(init.Files,CancellationToken.None);
            var clock=Stopwatch.StartNew();
            using var recognizer=new ParakeetRecognizer(init.Files,init.Threads);
            await Send(new {type="ready",loadMs=clock.Elapsed.TotalMilliseconds});
            while(await input.ReadLineAsync() is {} line) {
                if(line.Length>600000)throw new InvalidDataException("Audio window too large.");
                var request=JsonSerializer.Deserialize<DictationAudioRequest>(line)??throw new InvalidDataException();
                if(request.Exit)break;
                var bytes=Convert.FromBase64String(request.Pcm??"");
                if(bytes.Length is <2 or >400000||bytes.Length%2!=0)throw new InvalidDataException("Invalid PCM window.");
                var samples=new float[bytes.Length/2];for(var i=0;i<samples.Length;i++)samples[i]=BitConverter.ToInt16(bytes,i*2)/32768f;
                clock.Restart();var text=recognizer.Decode(samples);
                await Send(new {type="result",id=request.Id,text,decodeMs=clock.Elapsed.TotalMilliseconds});
            }
            return 0;
        }
        catch(Exception error){await Send(new {type="error",error=error.GetType().Name,message=error.Message});return 1;}
    }
}
