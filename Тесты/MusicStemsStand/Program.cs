using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AIHub.Services;

using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
var token = timeout.Token;
if (args.Contains("--prepare")) {
    Console.WriteLine("Preparing pinned separation components; no generation model download.");
    var last = DateTime.MinValue;
    await MusicStemsCatalog.PrepareAsync(true, new Progress<AIHub.Models.ComponentDownloadProgress>(p => {
        if ((DateTime.UtcNow - last).TotalSeconds < 2) return;
        last = DateTime.UtcNow; Console.WriteLine($"{p.ComponentId}: {p.Stage} {p.DownloadedBytes}/{p.TotalBytes}");
    }), token);
    Console.WriteLine("Components verified.");
}
var sourceIndex = Array.IndexOf(args, "--source");
if (sourceIndex >= 0) {
    var source = Path.GetFullPath(args[sourceIndex + 1]);
    var directory = Path.GetFullPath(args[Array.IndexOf(args, "--output") + 1]);
    Directory.CreateDirectory(directory);
    var runtime = MusicAudioRuntime.Default; await runtime.PrepareAsync(token);
    var clip = Path.Combine(directory, "source.wav");
    await runtime.RunAsync(false, ["-nostdin", "-v", "error", "-n", "-i", source, "-t", "9", "-ar", "48000", "-ac", "2", "-c:a", "pcm_s16le", "-f", "wav", clip], token);
    var clock = Stopwatch.StartNew();
    if (args.Contains("--pipeline")) {
        var jobs = new MusicGenerationJobs(Path.Combine(directory, "jobs"));
        var job = jobs.Create(directory, Path.Combine(directory, "results"), "Separation fixture — no model generation", 1, 9, "test fixture", "",
            outputSettings: new() { Format = MusicAudioFormat.Opus, AdditionalFormat = MusicAudioFormat.Mp3 }, separateTracks: true,
            comment: "Integration fixture: supplied PCM, generator is a test stub; separation and encoding are real.");
        var runner = new MusicGenerationRunner(jobs, new FixtureSynth(clip), stemsWorker: new MusicStemsWorker(), log: Console.WriteLine);
        await runner.RunAsync(job.Id, token);
        var track = jobs.Tracks(job.Id).Single();
        var mixed = await MusicStemPlayback.MixAsync(track, token, jobs);
        await File.WriteAllTextAsync(Path.Combine(directory, "pipeline.json"), JsonSerializer.Serialize(new { job.Id, track, mixed, elapsed = clock.Elapsed.TotalSeconds }, new JsonSerializerOptions { WriteIndented = true }), token);
        Console.WriteLine("Real separation + two formats + TXT + joint playback verified: " + track.BundleFolder);
        return;
    }
    var result = await new MusicStemsWorker(args.Contains("--cpu") ? "cpu" : "auto").SeparateAsync(clip, await MusicOutputPublisher.HashAsync(clip, token),
        Path.Combine(directory, "worker"), Console.WriteLine, token);
    await File.WriteAllTextAsync(Path.Combine(directory, "receipt.json"), JsonSerializer.Serialize(new { result, elapsed = clock.Elapsed.TotalSeconds }, new JsonSerializerOptions { WriteIndented = true }), token);
    Console.WriteLine(JsonSerializer.Serialize(new { result.Provider, result.Device, result.Gain, elapsed = clock.Elapsed.TotalSeconds }));
}

sealed class FixtureSynth(string source) : IMusicYueWorker
{
    public event Action<string>? Log { add { } remove { } }
    public Task PlanAsync(string model, MusicYueRequest request, string requestPath, string output, CancellationToken token) => File.WriteAllTextAsync(output, "X:1\nK:C\nC", token);
    public Task SynthesizeAsync(string model, string decoder, MusicYueRequest request, string requestPath, string output, CancellationToken token) {
        File.Copy(source, output, false); return Task.CompletedTask;
    }
}
