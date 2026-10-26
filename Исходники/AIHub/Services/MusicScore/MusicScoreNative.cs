using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.MusicScore;

/// <summary>Pinned CPU-only upstream parser. Its process never loads a model.</summary>
public static class MusicScoreNative
{
    public const string HelperHash = "82C760D35E6471B1FAA415196256EB4B5F3872B10C76EE4857F879C030A3FDE5";
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static async Task<MusicScoreInspection> InspectAsync(string abc,CancellationToken token) =>
        (await ExecuteAsync(new {operation="inspect",abc},token)).Deserialize<MusicScoreInspection>(Json)
        ?? throw new InvalidDataException("Music.Score.Invalid");
    public static async Task<MusicScoreInspection> InstrumentalAsync(string abc, CancellationToken token) =>
        (await ExecuteAsync(new { operation = "instrumental", abc }, token)).Deserialize<MusicScoreInspection>(Json)
        ?? throw new InvalidDataException("Music.Score.Invalid");
    public static async Task<string> MarkAsync(string abc,string style,string lyrics,string cot,bool keep,CancellationToken token) =>
        (await ExecuteAsync(new {operation="mark",abc,style,lyrics,cot,keep},token)).GetProperty("abc").GetString()!;
    public static async Task<bool> CanScheduleAsync(string abc,string lyrics,CancellationToken token)=>
        (await ExecuteAsync(new{operation="schedule",abc,lyrics},token)).GetProperty("sections").GetArrayLength()>0;
    public static async Task<MusicScoreInspection> MirrorAsync(string abc,int source,int[] targets,CancellationToken token) =>
        (await ExecuteAsync(new {operation="mirror",abc,source,targets},token)).Deserialize<MusicScoreInspection>(Json)
        ?? throw new InvalidDataException("Music.Score.Invalid");
    private static async Task<JsonElement> ExecuteAsync(object request,CancellationToken token) {
        var executable=Path.Combine(AppContext.BaseDirectory,"MusicScoreRuntime","lopata-score.exe");
        var manifest=Path.Combine(AppContext.BaseDirectory,"MusicScoreRuntime","helper.sha256");
        if(!File.Exists(executable)||!File.Exists(manifest)) throw new InvalidDataException("Music.Score.HelperMissing");
        // Build-time manifest is shipped with the fixed helper; no arbitrary executable or script path is accepted.
        await ComponentAudit.RunAsync("Music.Score.Parser", Path.GetDirectoryName(executable)!, HelperHash,
            ct => { ct.ThrowIfCancellationRequested();
                if (!File.Exists(executable) || !File.Exists(manifest)) throw new InvalidDataException("Music.Score.HelperMissing");
                return Task.CompletedTask; },
            async ct => { await using var file = File.OpenRead(executable);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
                if (hash != HelperHash || hash != File.ReadAllText(manifest).Trim()) throw new InvalidDataException("Music.Score.HelperMissing");
            }, token);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var info=new ProcessStartInfo(executable) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,
            RedirectStandardOutput=true,RedirectStandardError=true,StandardInputEncoding=new UTF8Encoding(false),
            StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
        using var process=OwnedProcessRegistry.Shared.Start(info,"Music.Score.Parser");
        try {
            var stdout=process.StandardOutput.ReadToEndAsync(deadline.Token);
            var stderr=process.StandardError.ReadToEndAsync(deadline.Token);
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(request,Json).AsMemory(),deadline.Token);
            process.StandardInput.Close(); await process.WaitForExitAsync(deadline.Token);
            var text=await stdout; _=await stderr;
            if(process.ExitCode!=0||text.Length>8_000_000) throw new InvalidDataException("Music.Score.Invalid");
            using var result=JsonDocument.Parse(text);
            if(!result.RootElement.GetProperty("ok").GetBoolean()) throw new InvalidDataException(result.RootElement.GetProperty("error").GetString());
            return result.RootElement.GetProperty("result").Clone();
        } finally { if(!process.HasExited) await OwnedProcessRegistry.Shared.StopAndWaitAsync(process.Id); }
    }
}
