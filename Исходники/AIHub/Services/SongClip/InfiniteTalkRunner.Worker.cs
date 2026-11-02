using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AIHub.Services.SongClip;

internal static partial class InfiniteTalkRunner
{
    internal static async Task RunWorkerAsync(string python,string request,string job,string action,
        Action<double,string> progress,CancellationToken token)
    {
        var info=new ProcessStartInfo(python) {UseShellExecute=false,CreateNoWindow=true,
            RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
        ManagedPythonLaunch.ScriptArguments(info,[Path.Combine(AppContext.BaseDirectory,"Tools","song_clip_infinitetalk.py"),action,"--request",request]);
        using var process=OwnedProcessRegistry.Shared.Start(info,"SongClip.InfiniteTalk");
        using var cancel=token.Register(()=>{try {if(!process.HasExited)process.Kill(true);}catch(InvalidOperationException){}});
        var diagnostics=new StringBuilder();
        var stderr=Task.Run(async()=>{
            while(await process.StandardError.ReadLineAsync() is {} line) {
                await File.AppendAllTextAsync(Path.Combine(job,"stderr.log"),line+Environment.NewLine,CancellationToken.None);
                diagnostics.AppendLine(line);if(diagnostics.Length>20000)diagnostics.Remove(0,12000);
            }
        });
        try {
            while(await process.StandardOutput.ReadLineAsync() is {} line) {
                await File.AppendAllTextAsync(Path.Combine(job,"progress.jsonl"),line+Environment.NewLine,CancellationToken.None);
                if(!line.StartsWith("@infinitetalk ",StringComparison.Ordinal)){progress(0,line);continue;}
                using var message=JsonDocument.Parse(line[14..]);var value=message.RootElement;
                var fraction=value.TryGetProperty("fraction",out var f)?f.GetDouble():0;
                if(!double.IsFinite(fraction))throw new InvalidDataException("Invalid InfiniteTalk progress.");
                progress(Math.Clamp(fraction,0,.98),value.GetRawText());
            }
            await process.WaitForExitAsync(CancellationToken.None);await stderr;token.ThrowIfCancellationRequested();
            if(process.ExitCode!=0)throw new IOException("InfiniteTalk: "+diagnostics);
            if(!File.Exists(Path.Combine(job,action=="--analyze"?"infinitetalk-analysis.json":"worker-result.json")))
                throw new InvalidDataException("SongClip.InfiniteResultInvalid");
        }
        finally {
            if(!process.HasExited)process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None);await stderr;
        }
    }
}
