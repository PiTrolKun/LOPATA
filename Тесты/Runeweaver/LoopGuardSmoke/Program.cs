using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;

var root = AppDataPaths.ProjectRoot ?? throw new Exception("Run from the source workspace.");
var output = Path.Combine(root, "Тесты/Runeweaver/loop_guard_app_20260909");
Directory.CreateDirectory(output);
Exception? uiError = null;
var ui = new Thread(() =>
{
    try
    {
        var app = new Application();
        var box = new TextBox();
        using var display = new LiteraryStreamDisplay(box);
        display.Report(new ModelStreamChunk("Неудачная попытка"));
        var prefix = "Неудачная попытка [Незавершённый ответ]\nПовтор:\n";
        display.Reset(prefix);
        display.Report(new ModelStreamChunk("Готовый ответ"));
        var completion = display.CompleteAsync();
        while (!completion.IsCompleted)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            box.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }
        completion.GetAwaiter().GetResult();
        if (display.Snapshot() != "Готовый ответ" || box.Text != prefix + "Готовый ответ")
            throw new Exception("Retry mixed the two attempt buffers.");
        app.Shutdown();
        Console.WriteLine("UI_RESET_OK");
    }
    catch (Exception error) { uiError = error; }
});
ui.SetApartmentState(ApartmentState.STA); ui.Start(); ui.Join();
if (uiError is not null) throw uiError;

using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
var token = deadline.Token;
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
var model = await LiteraryModelLocation.ResolveAsync(token);
using var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
var url = new Uri($"http://{IPAddress.Loopback}:{port}/");
var arguments = LiteraryChatRuntime.Arguments(model, port).ToList();
// Deterministic one-slot replay of the stand evidence; product pipeline and policy below are unchanged.
arguments[arguments.IndexOf("-c") + 1] = "16384";
arguments[arguments.IndexOf("-np") + 1] = "1";
arguments.Remove("-kvu");
var info = new ProcessStartInfo(LlamaBackendPaths.ServerExecutablePath)
{
    WorkingDirectory = LlamaBackendPaths.DirectoryPath, UseShellExecute = false, CreateNoWindow = true,
    RedirectStandardOutput = true, RedirectStandardError = true
};
foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("LLAMA_ARG_", StringComparison.Ordinal)).ToArray()) info.Environment.Remove(key);
foreach (var argument in arguments) info.ArgumentList.Add(argument);
using var process = new Process { StartInfo = info };
var logGate = new object();
void Log(string? line) { if (line is not null) lock (logGate) File.AppendAllText(Path.Combine(output, "server.log"), line + "\n"); }
process.OutputDataReceived += (_, e) => Log(e.Data); process.ErrorDataReceived += (_, e) => Log(e.Data);
process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
using var killDeadline = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
try
{
    while (true)
    {
        token.ThrowIfCancellationRequested();
        if (process.HasExited) throw new Exception("Backend exited.");
        try { using var health = await http.GetAsync(new Uri(url, "health"), token); if (health.IsSuccessStatusCode) break; }
        catch (HttpRequestException) { }
        await Task.Delay(200, token);
    }
    var path = Path.Combine(root, "Тесты/Runeweaver/loop_lab_20260909/guard_recovery/guard_seed_34-request.json");
    var original = JsonNode.Parse(await File.ReadAllTextAsync(path, token))!;
    var messages = original["messages"]!.AsArray().Select(m => new ImageAnalysisHiddenMessage
        { Role = m!["role"]!.GetValue<string>(), Content = m["content"]!.GetValue<string>() }).ToArray();
    var attempts = new List<object>(); var recovered = false;
    var result = await LiteraryLoopRecovery.RunAsync(async recovery =>
    {
        var body = JsonNode.Parse(LiteraryModelPolicy.Request(LiteraryChatProfile.Advisor, messages, recovery))!;
        body["id_slot"] = 0; body["seed"] = 34; body["cache_prompt"] = false;
        var name = recovery ? "retry" : "initial";
        await File.WriteAllTextAsync(Path.Combine(output, name + "-request.json"), body.ToJsonString(), token);
        var visible = new StringBuilder(); var watch = Stopwatch.StartNew();
        using var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Post, new Uri(url, "v1/chat/completions"))
            { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") }, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        try
        {
            var text = await LiteraryLoopStream.ReadAsync(stream, new Capture(visible), _ => { }, token);
            attempts.Add(new { name, finish = "stop", seconds = watch.Elapsed.TotalSeconds });
            return text;
        }
        catch (LiteraryLoopException error)
        {
            attempts.Add(new { name, finish = "loop_stopped", error.Evidence, seconds = watch.Elapsed.TotalSeconds });
            throw;
        }
        finally { await File.WriteAllTextAsync(Path.Combine(output, name + ".txt"), visible.ToString(), CancellationToken.None); }
    }, async evidence =>
    {
        using var idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(token); idleTimeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (true)
        {
            using var slots = JsonDocument.Parse(await http.GetStringAsync(new Uri(url, "slots"), idleTimeout.Token));
            if (slots.RootElement.EnumerateArray().All(s => !s.GetProperty("is_processing").GetBoolean())) break;
            await Task.Delay(100, idleTimeout.Token);
        }
        recovered = true;
        Console.WriteLine("LOOP_CANCEL_AND_IDLE_OK " + evidence);
    }, token);
    if (!recovered || attempts.Count != 2 || result.Length == 0) throw new Exception("The known loop/recovery was not reproduced.");
    File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new { recovered, attempts, ui = "passed", appVersion = typeof(LiteraryChatRuntime).Assembly.GetName().Version?.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("PRODUCT_LOOP_STREAM_RECOVERY_OK");
}
finally { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }

// Normal product runtime, including its own two-slot admission and process lifecycle.
using (var runtime = new LiteraryChatRuntime())
{
    var project = new LiteraryProject();
    foreach (var role in Enum.GetValues<LiteraryChatProfile>())
    {
        var reply = await runtime.SendAsync(role, [new() { Role = "user", Content = "Ответь одним словом: готов." }], "", project, null, token);
        if (string.IsNullOrWhiteSpace(reply)) throw new Exception("Empty runtime answer.");
        File.WriteAllText(Path.Combine(output, role + ".txt"), reply);
    }
    Console.WriteLine("BOTH_PRODUCT_ROLES_OK");
}
sealed class Capture(StringBuilder text) : IProgress<ModelStreamChunk>
{ public void Report(ModelStreamChunk value) => text.Append(value.Text); }
