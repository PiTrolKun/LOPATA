using System.Diagnostics;
using System.IO;
using System.Text;

namespace AIHub.Services;

/// <summary>One owned acoustic process; cancellation returns only after confirmed exit.</summary>
internal static class MusicLyricsWorkerProcess
{
    internal sealed record Outcome(int ExitCode, string Error);
    internal static async Task<Outcome> RunAsync(string python, string script, string request,
        Action<string> emit, CancellationToken token)
    {
        var info = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = Path.GetDirectoryName(python)! };
        foreach (var key in info.Environment.Keys.Where(k=>k.StartsWith("PYTHON",StringComparison.OrdinalIgnoreCase)).ToArray())
            info.Environment.Remove(key);
        info.Environment["PYTHONUTF8"] = "1"; info.Environment["PYTHONUNBUFFERED"] = "1";
        info.Environment["HF_HUB_OFFLINE"] = "1"; info.Environment["TRANSFORMERS_OFFLINE"] = "1";
        ManagedPythonLaunch.ScriptArguments(info,[script,"--request",request]);
        token.ThrowIfCancellationRequested();
        using var process = OwnedProcessRegistry.Shared.Start(info,"Music.LyricsAlignment");
        var errors = new StringBuilder();
        async Task Pump(StreamReader reader, bool error) {
            while(await reader.ReadLineAsync() is { } line) {
                emit(line[..Math.Min(line.Length,4096)]);
                if(error) { errors.AppendLine(line); if(errors.Length>8192) errors.Remove(0,errors.Length-8192); }
            }
        }
        var stdout=Pump(process.StandardOutput,false); var stderr=Pump(process.StandardError,true);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch(OperationCanceledException) {
            if(!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None); await Task.WhenAll(stdout,stderr);
            token.ThrowIfCancellationRequested(); throw new TimeoutException("Lyrics acoustic search timed out.");
        }
        finally {
            if(!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None);
        }
        await Task.WhenAll(stdout,stderr); token.ThrowIfCancellationRequested();
        return new(process.ExitCode,errors.ToString());
    }
}
