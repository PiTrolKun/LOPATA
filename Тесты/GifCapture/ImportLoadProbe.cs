using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.LiteraryImport;

internal static class ImportLoadProbe
{
    public static async Task Run(string run)
    {
        var root = Path.Combine(run, "literary-import"); Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, ".creation"), "GIF coexistence fixture");
        var source = Path.Combine(root, "source.json"); File.WriteAllText(source, "[]");
        using var runtime = new LiteraryChatRuntime(root, preparing: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var licenses = new ComponentLicenseService(Path.Combine(AppContext.BaseDirectory, "Licenses"), Path.Combine(AppDataPaths.BaseDirectory, "Licenses", "receipts.json"));
        // Read existing acknowledgements only. A test cannot grant acceptance for the user.
        ComponentLicenseGate.ConfirmAsync = (ids, ct) => licenses.EnsureAsync(ids, _ => Task.FromResult(false), ct);
        var samples = new List<object>(); var phase = "warm-up";
        using var samplingStop = new CancellationTokenSource();
        var sampling = Task.Run(async () =>
        {
            while (!samplingStop.IsCancellationRequested)
            {
                try
                {
                    var owned = (Process?)typeof(LiteraryChatRuntime).GetField("_process", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(runtime);
                    owned?.Refresh();
                    var gpu = "unavailable";
                    try
                    {
                        using var query = Process.Start(new ProcessStartInfo("nvidia-smi", "--query-gpu=utilization.gpu,memory.used,memory.total --format=csv,noheader,nounits")
                            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
                        gpu = await query!.StandardOutput.ReadToEndAsync(); await query.WaitForExitAsync();
                    }
                    catch (Exception error) { gpu = error.GetType().Name; }
                    samples.Add(new { phase, utc = DateTime.UtcNow, hostRss = Process.GetCurrentProcess().WorkingSet64,
                        hostCpuSeconds = Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds,
                        modelPid = owned?.Id, modelRss = owned is { HasExited: false } ? owned.WorkingSet64 : 0,
                        modelCpuSeconds = owned is { HasExited: false } ? owned.TotalProcessorTime.TotalSeconds : 0, gpu });
                    await Task.Delay(1000, samplingStop.Token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception error) { samples.Add(new { phase, samplingError = error.Message }); }
            }
        });
        var units = Enumerable.Range(0, 6).Select(i => new ImportUnit("unit-" + i, "conversation", "message-" + i, "", "assistant", i, 0,
            "Роман «Ключ у двери». Глава «Возвращение». " + string.Concat(Enumerable.Repeat("Мирон вернулся к закрытой двери и проверил латунный ключ. Он вспоминал обещание инженеру и слушал ветер за окном. Дверь осталась закрытой; Мирон решил дождаться утра. ", 32)), false)).ToList();
        var input = new ImportInput([new("conversation", "Fictional import load", units.Count)], units, [], []);
        var progress = new Progress<ImportProgress>(p => Console.WriteLine($"IMPORT {p.Done}/{p.Total}"));
        var timings = new List<object>();
        async Task<ImportDecision[]> Analyze(string name)
        {
            phase = name;
            using var session = ImportSession.Create(root, source, timeout.Token);
            var pipeline = new ImportPipeline(session, (messages, step, tokens, ct) => runtime.ImportAnalyzeAsync(messages, session, step, tokens, ct));
            var clock = Stopwatch.StartNew(); var decisions = await pipeline.AnalyzeAsync(input, ["conversation"], progress, timeout.Token);
            timings.Add(new { name, seconds = clock.Elapsed.TotalSeconds, decisions = decisions.Length, session = session.Root });
            Console.WriteLine($"IMPORT {name}: {clock.Elapsed.TotalSeconds:F2}s, {decisions.Length} decisions"); return decisions;
        }
        using (var warm = ImportSession.Create(root, source, timeout.Token))
            await runtime.ImportAnalyzeAsync([new() { Role = "system", Content = "Return only JSON: {\"ok\":true}" }, new() { Role = "user", Content = "Warm up" }], warm, "warm-up", 2048, timeout.Token);
        var baseline = await Analyze("baseline");
        var display = CaptureDisplayGeometry.AtCursor();
        var recording = new GifRecordingSession(new() { Folder = run, GifSeconds = 60, GifScalePercent = 100, GifFps = 10, Processing = "cpu" }, CaptureSource.Monitor);
        var progressFrames = new List<GifRecordingProgress>(); recording.Progress += p => { lock (progressFrames) progressFrames.Add(p); };
        var capture = recording.RunAsync(display.Handle, false, display.Bounds, timeout.Token);
        var concurrent = await Analyze("concurrent-cpu-gif"); recording.Stop(); var gif = await capture;
        var hardware = new GifRecordingSession(new() { Folder = run, GifSeconds = 60, GifScalePercent = 25, GifFps = 10, Processing = "gpu" }, CaptureSource.Monitor);
        var hardwareTask = hardware.RunAsync(display.Handle, false, display.Bounds, timeout.Token);
        var hardwareImport = await Analyze("concurrent-wgc-gif"); hardware.Stop(); var hardwareGif = await hardwareTask;
        samplingStop.Cancel(); await sampling;
        File.WriteAllText(Path.Combine(run, "import-load.json"), JsonSerializer.Serialize(new { model = LiteraryModelLocation.FileName, timings,
            gif, hardwareGif, samples, frames = progressFrames.Count(p => !p.Assembling), maxWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64 }, new JsonSerializerOptions { WriteIndented = true }));
        if (baseline.Length != units.Count || concurrent.Length != units.Count || hardwareImport.Length != units.Count || gif.File is null || gif.Error.Length > 0 || hardwareGif.File is null || hardwareGif.Error.Length > 0)
            throw new Exception("Concurrent import/GIF did not complete successfully.");
        Console.WriteLine("PASS: real literary import completes with CPU GIF; raw requests/responses preserved in sessions.");
    }
}
