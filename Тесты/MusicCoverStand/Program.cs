using System.Text.Json;
using System.IO;
using AIHub.Services;
using AIHub.Models;
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(40)); var token = deadline.Token;
var mode = args[0]; var output = Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
if (mode == "prepare") { await MusicCoverScoreCatalog.PrepareAsync(true, new Progress<ComponentDownloadProgress>(p => Console.WriteLine(p.ComponentId + " " + p.Stage + " " + p.DownloadedBytes)), token); Console.WriteLine("Score models verified"); return; }
var source = Path.GetFullPath(args[2]); var root = Path.GetFullPath(args[3]);
if (mode == "verify-fit") {
    var savedJob = JsonSerializer.Deserialize<MusicGenerationJob>(await File.ReadAllTextAsync(source, token))!;
    var fitted = savedJob.Variants[0].AudioTask!;
    await MusicOutputPublisher.MatchHashAsync(fitted.FittedLayerPath!, fitted.FittedLayerHash!, token);
    var aligner = new MusicLyricsAlignmentService(); aligner.Log += Console.WriteLine;
    var measured = await aligner.AlignAsync(fitted.FittedLayerPath!, savedJob.Lyrics, Path.Combine(output, "fit-verification"), token);
    await File.WriteAllTextAsync(Path.Combine(output, "fitted-timing.json"), JsonSerializer.Serialize(measured), token);
    Console.WriteLine("Fitted verification: " + measured.State + "; phrases=" + measured.Lines.Length); return;
}
if (mode == "finish-job") {
    var savedJob = JsonSerializer.Deserialize<MusicGenerationJob>(await File.ReadAllTextAsync(source, token))!;
    var store = MusicGenerationJobs.Default; savedJob = store.Load(savedJob.Id);
    if (savedJob.Variants.Any(v => v.Completed)) throw new InvalidDataException("Expected the unfinished user attempt");
    File.Copy(source, Path.Combine(output, "job-before.json"), false);
    var originalReceipt = savedJob.Variants[0].AudioTask!;
    var keptMusic = originalReceipt.OriginalStems!.MusicPcm;
    var before = await MusicAudioTaskFiles.HashAsync(keptMusic, token);
    var noGenerator = new StandNoGenerator();
    await new MusicGenerationRunner(store, noGenerator, log: Console.WriteLine).RunAsync(savedJob.Id, token);
    var finished = store.Load(savedJob.Id); var finishedVariant = finished.Variants[0];
    if (before != await MusicAudioTaskFiles.HashAsync(keptMusic, token)) throw new InvalidDataException("Kept original music changed");
    var result = new { jobId = finished.Id, generationCalls = noGenerator.Calls, finishedVariant.Completed,
        resultFile = finishedVariant.ResultPath, finishedVariant.ResultHash, finishedVariant.DurationSeconds,
        alignment = finishedVariant.AudioTask!.Alignment, originalMusicUnchanged = true, postprocessVersion = MusicSongMetadata.AppVersion };
    await File.WriteAllTextAsync(Path.Combine(output, "finished.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }), token);
    Console.WriteLine("Finished saved user take: " + finishedVariant.ResultPath); return;
}
if (mode == "align-job") {
    var savedJob = JsonSerializer.Deserialize<MusicGenerationJob>(await File.ReadAllTextAsync(source, token))!;
    var receipt = savedJob.Variants[0].AudioTask!;
    var aligner = new MusicLyricsAlignmentService(); aligner.Log += Console.WriteLine;
    foreach (var (name, audio, words) in new[] { ("original", receipt.OriginalStems!.VoicePcm, savedJob.AudioTask!.SourceLyrics), ("fresh", receipt.RenderedStems!.VoicePcm, savedJob.Lyrics) }) {
        var result = await aligner.AlignAsync(audio, words, Path.Combine(output, name), token);
        await File.WriteAllTextAsync(Path.Combine(output, name + ".json"), JsonSerializer.Serialize(result), token);
        Console.WriteLine(name + ": " + result.State + "; phrases=" + result.Lines.Length);
    }
    return;
}
if (mode == "score") { var score = await new MusicCoverScoreWorker().TranscribeAsync(root, source, false, Console.WriteLine, token); await File.WriteAllTextAsync(Path.Combine(output,"score.abc"),score,token); Console.WriteLine("Score saved"); return; }
if (mode == "recognize") {
    var clock = System.Diagnostics.Stopwatch.StartNew();
    var text = await MusicSourceLyrics.RecognizeAsync(source, token, 30, 45);
    if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("No recognized text");
    var evidence = new { characters = text.Length, nonempty = true, seconds = clock.Elapsed.TotalSeconds, start = 30, end = 45, sourceSha256 = await MusicAudioTaskFiles.HashAsync(source,token) };
    await File.WriteAllTextAsync(Path.Combine(output,"recognition.json"), JsonSerializer.Serialize(evidence,new JsonSerializerOptions {WriteIndented=true}),token);
    Console.WriteLine("Real recognition returned nonempty text: " + text.Length + " characters"); return;
}
var studio = mode == "studio";
var expert = MusicModelVariants.Defaults(studio ? MusicStudioRuntime.Variation : MusicAceCatalog.Variation);
if (studio) { expert.Values["cot"] = 1; expert.Values["steps"] = 8; } else expert.Values["thinking"] = 0;
var task = new MusicAudioTask { Enabled = true, Mode = mode == "refs" ? MusicAudioTaskMode.NewSong : mode == "repaint" ? MusicAudioTaskMode.Repaint : MusicAudioTaskMode.Cover,
 Source = new(source), EditStart = 2, EditEnd = 5, References = mode == "refs" ? [new(source) { Start=0, End=3 },new(source) { Start=4,End=8 }] : [] };
var jobs = new MusicGenerationJobs(Path.Combine(output,"jobs")); var job = jobs.Create(root,Path.Combine(output,"results"),"Cover physical " + mode,1,9,"instrumental acoustic guitar, warm folk", "",expert:expert,wishes:MusicWishSnapshot.Capture(new MusicPreferences { Instrumental=true }),outputSettings:new() {Format=MusicAudioFormat.Wav},durationAutomatic:false);
var captured = await MusicAudioTaskFiles.CaptureAsync(task, jobs.Folder(job.Id), expert.Variation, token); job = job with { AudioTask = captured }; jobs.Save(job);
IMusicYueWorker worker = studio ? new MusicStudioWorker() : new MusicAceWorker(); worker.Log += Console.WriteLine;
await new MusicGenerationRunner(jobs,worker,log:Console.WriteLine).RunAsync(job.Id,token);
await File.WriteAllTextAsync(Path.Combine(output,"physical-result.json"),JsonSerializer.Serialize(jobs.Load(job.Id),new JsonSerializerOptions {WriteIndented=true}),token); Console.WriteLine("Physical task completed: " + job.Id);

sealed class StandNoGenerator : IMusicYueWorker
{
    public int Calls;
    public event Action<string>? Log { add { } remove { } }
    public Task PlanAsync(string model, MusicYueRequest request, string requestPath, string planPath, CancellationToken token) { Calls++; throw new InvalidOperationException("Test must reuse the saved plan"); }
    public Task SynthesizeAsync(string model, string decoder, MusicYueRequest request, string requestPath, string outputPath, CancellationToken token) { Calls++; throw new InvalidOperationException("Test must reuse the saved take"); }
}
