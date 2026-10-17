using System.Diagnostics;
using System.IO;
using System.Text;

namespace AIHub.Services;

/// <summary>Local executables only, owned lifetime, bounded output and no shell.</summary>
internal static class FileMetadataProcess
{
    internal static async Task<byte[]> RunAsync(string executable, IEnumerable<string> arguments,
        CancellationToken token, string? input = null, int maximumBytes = 32 * 1024 * 1024)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(45));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input is not null,
            WorkingDirectory = Path.GetDirectoryName(executable)! };
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("PERL", StringComparison.OrdinalIgnoreCase)
            || k.StartsWith("LC_", StringComparison.OrdinalIgnoreCase) || k == "LANG").ToArray()) start.Environment.Remove(key);
        start.Environment["LANG"] = "C";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        token.ThrowIfCancellationRequested();
        using var process = OwnedProcessRegistry.Shared.Start(start, "Metadata.Local");
        using var cancel = limit.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); }
            catch (InvalidOperationException) { } });
        async Task<byte[]> Read(Stream stream, int max)
        {
            using var bytes = new MemoryStream(); var buffer = new byte[16384]; int count;
            while ((count = await stream.ReadAsync(buffer, limit.Token)) > 0)
            {
                if (bytes.Length + count > max) { limit.Cancel(); throw new InvalidDataException("Metadata.TooLarge"); }
                bytes.Write(buffer, 0, count);
            }
            return bytes.ToArray();
        }
        var output = Read(process.StandardOutput.BaseStream, maximumBytes);
        var error = Read(process.StandardError.BaseStream, 65536);
        try
        {
            if (input is not null)
            {
                await process.StandardInput.BaseStream.WriteAsync(Encoding.UTF8.GetBytes(input), limit.Token);
                process.StandardInput.Close();
            }
            await Task.WhenAll(output, error, process.WaitForExitAsync(limit.Token));
            if (process.ExitCode != 0)
            {
                var diagnostic=Encoding.UTF8.GetString(await error);if(diagnostic.Length>2000)diagnostic=diagnostic[..2000];
                OwnedProcessRegistry.Log("metadata_process_failed","Metadata.Local",process.Id,Path.GetFileName(executable)+"; exit="+process.ExitCode+"; "+diagnostic);
                throw new InvalidDataException("Metadata.ReadFailed");
            }
            return await output;
        }
        catch (OperationCanceledException) { token.ThrowIfCancellationRequested(); throw new TimeoutException("Metadata.TimedOut"); }
        finally
        {
            try { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); }
            finally { try { await Task.WhenAll(output, error); } catch (Exception) { /* Both pipes observed after terminating the owned process. */ } }
        }
    }
}
