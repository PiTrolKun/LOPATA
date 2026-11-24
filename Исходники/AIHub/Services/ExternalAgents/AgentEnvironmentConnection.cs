using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Lopata.Island;

namespace AIHub.Services.ExternalAgents;

internal sealed record AgentEnvironmentRequest(string Action, string Scenario, string Operation, AgentIdentity? Client = null);

// Component acquisition belongs to the running desktop host, which owns the license UI and job state.
// Queries about saved logs or plain project drafts never need this connection.
internal static class AgentEnvironmentConnection
{
    private static string PipeName => IslandLink.InstallationName(AppContext.BaseDirectory) + ".AgentPreparation";
    private const PipeOptions Options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    internal static async Task<object> RequestAsync(string action, string scenario, string operation, AgentIdentity? client = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, Options);
        try { await pipe.ConnectAsync(400, timeout.Token); }
        catch (TimeoutException)
        {
            if (action is not ("environment_prepare" or "environment_check")
#if DEBUG
                && action is not ("dev_open_scenario" or "dev_desktop")
#endif
               )
                return new { scenario, operation, state = "desktop_closed" };
            using var started = Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "AIHub.exe"))
            {
                UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory
            });
            await pipe.ConnectAsync(40000, timeout.Token);
        }
        using var writer = new StreamWriter(pipe, Utf8, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Utf8, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(new AgentEnvironmentRequest(action, scenario, operation, client)));
        var response = await reader.ReadLineAsync(timeout.Token) ?? throw new IOException("LOPATA closed during preparation.");
        return JsonSerializer.Deserialize<JsonElement>(response);
    }

    internal static async Task ListenAsync(Func<AgentEnvironmentRequest, object> handle, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, Options);
                await pipe.WaitForConnectionAsync(stop).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                using var reader = new StreamReader(pipe, Utf8, leaveOpen: true);
                using var writer = new StreamWriter(pipe, Utf8, leaveOpen: true) { AutoFlush = true };
                var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                if (line is null) continue;
                object result;
                try
                {
                    var request = JsonSerializer.Deserialize<AgentEnvironmentRequest>(line) ?? throw new InvalidDataException("Invalid request.");
                    result = handle(request);
                }
                catch (Exception ex) { result = new { state = "error", reason = ex.Message }; }
                await writer.WriteLineAsync(JsonSerializer.Serialize(result, AgentProtocol.Json).AsMemory(), timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (OperationCanceledException) { }
            catch (IOException) { await Task.Delay(150).ConfigureAwait(false); }
        }
    }
}
