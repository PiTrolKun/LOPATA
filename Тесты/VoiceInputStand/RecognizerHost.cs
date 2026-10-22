using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIHub.Services;

namespace VoiceInputStand;

public sealed record RecognitionResult(string Text, double Milliseconds);

public sealed class RecognizerHost(StandLog log, string root, string data) : IAsyncDisposable
{
    private Process? _process;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RecognitionResult>> _pending = new();
    private TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _outputReader, _errorReader;
    private readonly SemaphoreSlim _serial = new(1, 1);
    public string Engine { get; private set; } = "";
    public bool IsReady { get; private set; }
    public event Action<string,string>? Preview;
    public event Action<JsonElement>? Telemetry;

    private async Task AuditAsync(string engine, CancellationToken token)
    {
        using var models = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(data, "models.json"), token));
        var model = models.RootElement.GetProperty(engine);
        using var runtime = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(data, "runtime-files.json"), token));
        var rows = model.GetProperty("files").EnumerateArray().Select(x => x.Clone())
            .Concat(runtime.RootElement.EnumerateArray().Select(x => x.Clone())).ToArray();
        var revision = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", rows.Select(x=>x.GetRawText())))));
        var enabled = true;
        if (File.Exists(AppDataPaths.SettingsPath))
        {
            using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(AppDataPaths.SettingsPath, token));
            if (settings.RootElement.TryGetProperty("behavior", out var behavior) && behavior.TryGetProperty("componentAuditEnabled", out var flag)) enabled=flag.GetBoolean();
        }
        log.Write("audit_begin", new {engine, enabled, revision, files=rows.Length, isolated_receipts=true});
        using var scope = ComponentAudit.UseTestStore(Path.Combine(data,"audit"), enabled);
        Task Presence(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var row in rows) if (!File.Exists(row.GetProperty("path").GetString())) throw new FileNotFoundException("Missing stand component", row.GetProperty("path").GetString());
            return Task.CompletedTask;
        }
        async Task Full(CancellationToken ct)
        {
            await Presence(ct);
            foreach (var row in rows)
            {
                var path=row.GetProperty("path").GetString()!;
                await using var stream=File.OpenRead(path);
                var hash=Convert.ToHexStringLower(await SHA256.HashDataAsync(stream,ct));
                if(hash != row.GetProperty("sha256").GetString()) throw new InvalidDataException("Stand component hash mismatch: "+path);
                log.Write("audit_file_verified",new {path,hash});
            }
        }
        await ComponentAudit.RunAsync("voice-stand."+engine, model.GetProperty("directory").GetString()!, revision, Presence, Full, token, text=>log.Write("audit",text));
        log.Write("audit_complete",new{engine});
    }

    public async Task LoadAsync(string engine, int threads, CancellationToken token)
    {
        await DisposeAsync();
        await AuditAsync(engine,token);
        Engine=engine; IsReady=false;
        _ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
        var start=new ProcessStartInfo(Path.Combine(root,"Тесты","VoiceInputStand",".venv","Scripts","python.exe"))
        {
            WorkingDirectory=data, UseShellExecute=false, CreateNoWindow=true,
            RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,
            StandardInputEncoding=new UTF8Encoding(false), StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8
        };
        foreach(var arg in new[]{Path.Combine(root,"Тесты","VoiceInputStand","worker.py"),"--data",data,"--engine",engine,"--threads",threads.ToString()}) start.ArgumentList.Add(arg);
        start.Environment["PYTHONUTF8"]="1"; start.Environment["PYTHONUNBUFFERED"]="1";
        start.Environment["OMP_NUM_THREADS"]=threads.ToString();
        _process=Process.Start(start) ?? throw new IOException("Could not start recognizer");
        var process=_process;
        log.Write("worker_spawn",new{engine,pid=process.Id,command=start.FileName,args=start.ArgumentList.ToArray(),threads});
        _outputReader = ReadOutput(process,_ready);
        _errorReader = Task.Run(async()=>{while(await process.StandardError.ReadLineAsync() is {} line) log.Write("worker_stderr",new{pid=process.Id,line});});
        await _ready.Task.WaitAsync(TimeSpan.FromMinutes(3),token);
        IsReady=true;
    }
    private async Task ReadOutput(Process process,TaskCompletionSource ready)
    {
        try
        {
            while(await process.StandardOutput.ReadLineAsync() is {} line)
            {
                log.Write("worker_stdout",new{pid=process.Id,line});
                using var doc=JsonDocument.Parse(line);
                var row=doc.RootElement;
                switch(row.GetProperty("event").GetString())
                {
                    case "ready": ready.TrySetResult(); break;
                    case "preview": Preview?.Invoke(row.GetProperty("id").GetString()!,row.GetProperty("text").GetString()!);break;
                    case "result":
                        if(_pending.TryRemove(row.GetProperty("id").GetString()!,out var task)) task.TrySetResult(new(row.GetProperty("text").GetString()!,row.GetProperty("decode_ms").GetDouble()));
                        break;
                    case "error": throw new IOException(row.GetProperty("traceback").GetString());
                }
                Telemetry?.Invoke(row.Clone());
            }
            await process.WaitForExitAsync();
            if(_process!=process){log.Write("worker_eof",new{pid=process.Id,exitCode=process.ExitCode});return;}
            throw new IOException("Recognizer process exited: "+process.ExitCode);
        }
        catch(Exception error)
        {
            log.Write("worker_failure",error.ToString());
            ready.TrySetException(error);
            if(_process==process){IsReady=false;foreach(var pair in _pending) pair.Value.TrySetException(error);}
        }
    }
    public async Task<RecognitionResult> DecodeAsync(string id,string path,CancellationToken token)
    {
        await _serial.WaitAsync(token);
        try
        {
            if(!IsReady || _process is null || _process.HasExited) throw new IOException("Recognizer is not ready");
            var promise=new TaskCompletionSource<RecognitionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id]=promise;
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new{id,path}));
            await _process.StandardInput.FlushAsync(token);
            return await promise.Task.WaitAsync(TimeSpan.FromMinutes(3),token);
        }
        finally {_pending.TryRemove(id,out _);_serial.Release();}
    }
    public async ValueTask DisposeAsync()
    {
        IsReady=false;
        var process=_process;_process=null;
        if(process is null) return;
        if(!process.HasExited)
        {
            try {await process.StandardInput.WriteLineAsync("{\"command\":\"exit\"}");await process.StandardInput.FlushAsync();await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));}
            catch(Exception error) when(error is IOException or TimeoutException or InvalidOperationException)
            {if(!process.HasExited) process.Kill(entireProcessTree:true);await process.WaitForExitAsync();}
        }
        foreach(var pair in _pending) pair.Value.TrySetCanceled();
        if(_outputReader is {} output) await output;
        if(_errorReader is {} errors) await errors;
        log.Write("worker_stopped",new{pid=process.Id,exitCode=process.ExitCode});
        process.Dispose();
    }
}
