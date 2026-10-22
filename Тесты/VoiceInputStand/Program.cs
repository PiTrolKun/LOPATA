using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace VoiceInputStand;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var root=Path.GetFullPath(args.FirstOrDefault() ?? Directory.GetCurrentDirectory());
        var data=Path.Combine(root,"_tmp","voice-input-stand");
        Directory.CreateDirectory(data);
        if(args.Length>=4 && args[1]=="--smoke")
        {
            try{Smoke(root,data,args[2],args[3]).GetAwaiter().GetResult();}
            catch(Exception error){File.WriteAllText(Path.Combine(data,"smoke-failure-"+args[2]+".txt"),error.ToString());Environment.ExitCode=1;}
            return;
        }
        var app=new Application();
        app.DispatcherUnhandledException+=(_,e)=>
        {
            File.AppendAllText(Path.Combine(data,"ui-failures.txt"),DateTimeOffset.UtcNow+" "+e.Exception+"\n");
            MessageBox.Show(e.Exception.Message,"Ошибка стенда");e.Handled=true;
        };
        app.Run(new StandWindow(root,data));
    }
    private static async Task Smoke(string root,string data,string engine,string wav)
    {
        using var log=new StandLog(data);
        await using var host=new RecognizerHost(log,root,data);
        var previews=0;
        host.Preview+=(_,_)=>Interlocked.Increment(ref previews);
        var clock=Stopwatch.StartNew();
        await host.LoadAsync(engine,4,CancellationToken.None);
        var load=clock.Elapsed.TotalMilliseconds;
        wav=AudioSession.NormalizeFile(Path.GetFullPath(wav),log.DirectoryPath);
        var result=await host.DecodeAsync(Guid.NewGuid().ToString("N"),wav,CancellationToken.None);
        if(string.IsNullOrWhiteSpace(result.Text)) throw new InvalidDataException("Empty recognition result");
        var warm=await host.DecodeAsync(Guid.NewGuid().ToString("N"),wav,CancellationToken.None);
        var proof=new{engine,load_ms=load,first=result,warm,previews,logs=log.DirectoryPath};
        File.WriteAllText(Path.Combine(data,"smoke-"+engine+".json"),JsonSerializer.Serialize(proof,new JsonSerializerOptions{WriteIndented=true}));
    }
}
