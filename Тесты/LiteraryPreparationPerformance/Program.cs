using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AIHub.Services;

var output = Path.GetFullPath("Тесты/LiteraryPreparationPerformance/runs/" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
Directory.CreateDirectory(output);
using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(8));
var records = new List<object>();
foreach (var force in args.Contains("--quick-only") ? new[] { false } : new[] { true, false })
{
    var timer = Stopwatch.StartNew(); var previous = ""; var started = 0d; var steps = new List<object>();
    var progress = new ProbeProgress(p =>
    {
        var stage = p.Stage + " / " + p.Detail;
        if (stage == previous) return;
        if (previous.Length > 0)
        {
            var elapsed = timer.Elapsed.TotalSeconds - started;
            steps.Add(new { stage = previous, seconds = elapsed }); Console.WriteLine($"  {previous}: {elapsed:F3}s");
        }
        previous = stage; started = timer.Elapsed.TotalSeconds;
        Console.WriteLine($"{(force ? "FULL" : "QUICK")} {stage}");
    });
    var states = await LiteraryPreparation.CheckAsync(progress, cancel.Token, force);
    steps.Add(new { stage = previous, seconds = timer.Elapsed.TotalSeconds - started });
    records.Add(new { force, seconds = timer.Elapsed.TotalSeconds, states, steps });
    Console.WriteLine($"{(force ? "FULL" : "QUICK")} total {timer.Elapsed.TotalSeconds:F3}s; ready={states.All(s => s.Ready)}");
    File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
}
Console.WriteLine(output);

sealed class ProbeProgress(Action<LiteraryPreparationProgress> action) : IProgress<LiteraryPreparationProgress>
{
    public void Report(LiteraryPreparationProgress value) => action(value);
}
