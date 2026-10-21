using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.MusicMidi;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicMidiTests
{
    internal static MusicMelody Tune() => new() { Bpm = 120, Enabled = true, Notes = [
        new(60,0,480), new(64,480,480), new(67,1200,240), new(72,1440,480)] };
    [TestMethod]
    public void InvalidEditIsAtomicAndUndoRedoRestoreExactTiming() {
        var tune = Tune(); var editor = new MusicMelodyEditor(tune); var ids = new HashSet<string>(tune.Notes.Take(2).Select(n => n.Id));
        editor.Transpose(ids,2); Assert.AreEqual(62,editor.Current.Notes[0].Pitch);
        Assert.Throws<InvalidDataException>(() => editor.Repeat(ids)); // Would overlap the later phrase.
        Assert.AreEqual(4,editor.Current.Notes.Length); editor.Undo(); Assert.AreEqual(60,editor.Current.Notes[0].Pitch);
        editor.Redo(); Assert.AreEqual(62,editor.Current.Notes[0].Pitch);
        editor.Remove(new HashSet<string>(tune.Notes.Select(n => n.Id))); Assert.IsFalse(editor.Current.Enabled); Assert.HasCount(0,editor.Current.Notes);
        editor.Undo(); Assert.HasCount(4,editor.Current.Notes);
        editor.Repeat(new HashSet<string>(editor.Current.Notes.Select(n => n.Id))); Assert.HasCount(8,editor.Current.Notes);
        Assert.AreEqual(3840,editor.Current.EndTick);
    }
    [TestMethod]
    public void InvalidExternalDataAndExcessiveMelodiesAreRefused() {
        Assert.Throws<InvalidDataException>(() => (Tune() with { Notes = [null!] }).Validate());
        Assert.Throws<InvalidDataException>(() => (Tune() with { Bpm = 0 }).Validate());
        Assert.Throws<InvalidDataException>(() => (Tune() with { Notes = [new(128,0,480)] }).Validate());
        Assert.Throws<InvalidDataException>(() => (Tune() with { Notes = [new(60,-60,480)] }).Validate());
        Assert.Throws<InvalidDataException>(() => (Tune() with { Notes = [new(60,0,480),new(61,240,480)] }).Validate());
        Assert.Throws<InvalidDataException>(() => (Tune() with { Notes = [new(60,0,480*721)] }).Validate());
        Assert.Throws<InvalidDataException>(() => (Tune() with { ImportWarnings = ["untrusted localization key"] }).Validate());
    }
    [TestMethod]
    public void StandardMidiAndMelodyProjectPreserveNotesTempoAndEmbeddedOrigin() {
        using var f = new MusicProjectTests.Files(); var path = Path.Combine(f.Root,"melody.mid");
        Directory.CreateDirectory(f.Root);
        var tune = Tune(); MusicMidiFile.Write(path,tune);
        var imported = MusicMidiFile.Read(path).Single().Melody;
        CollectionAssert.AreEqual(tune.Notes.Select(n => (n.Pitch,n.Start,n.Length,n.Velocity)).ToArray(),imported.Notes.Select(n => (n.Pitch,n.Start,n.Length,n.Velocity)).ToArray());
        Assert.AreEqual(tune.Bpm,imported.Bpm);
        var metadata = MidiFile.Read(path).GetTrackChunks().Single().Events.OfType<TextEvent>().Single().Text;
        using var json = JsonDocument.Parse(metadata); Assert.AreEqual("LOPATA",json.RootElement.GetProperty("app").GetString());
        Assert.AreEqual(MusicSongMetadata.AppVersion,json.RootElement.GetProperty("version").GetString());
        var project = Path.Combine(f.Root,"saved.lopata-melody.json"); MusicMidiFile.SaveProject(project,tune with { SourceName = Path.Combine(f.Root,"source.mid") });
        var opened = MusicMidiFile.ReadProject(project); Assert.AreEqual(tune.Notes[0].Id,opened.Notes[0].Id); Assert.IsTrue(opened.Enabled);
        Assert.IsFalse(File.ReadAllText(project).Contains(f.Root.Replace("\\","\\\\")));
        var evidence = Path.Combine(AppContext.BaseDirectory,"music-midi"); Directory.CreateDirectory(evidence);
        File.Copy(path,Path.Combine(evidence,"roundtrip.mid"),true);
    }
    [TestMethod]
    public void ComplexMidiSeparatesChannelsAndReportsDiscardedHarmonyAndTempo() {
        using var f = new MusicProjectTests.Files(); var path = Path.Combine(f.Root,"complex.mid");
        Directory.CreateDirectory(f.Root);
        var track = new TrackChunk(new SetTempoEvent(500000),new SequenceTrackNameEvent("Parts"));
        using(var manager = track.ManageNotes()) {
            manager.Objects.Add(new Note((SevenBitNumber)60,960,0));
            manager.Objects.Add(new Note((SevenBitNumber)72,240,240));
            manager.Objects.Add(new Note((SevenBitNumber)65,480,0) { Channel = (FourBitNumber)1 });
            manager.Objects.Add(new Note((SevenBitNumber)36,480,0) { Channel = (FourBitNumber)9 });
        }
        track.Events.Add(new SetTempoEvent(1000000) { DeltaTime = 480 });
        new MidiFile(track).Write(path);
        var bytes = File.ReadAllBytes(path); var choices = MusicMidiFile.Read(path);
        Assert.HasCount(2,choices); Assert.IsTrue(choices[0].Polyphonic);
        CollectionAssert.Contains(choices[0].Melody.ImportWarnings,"PolyphonyFlattened");
        CollectionAssert.Contains(choices[0].Melody.ImportWarnings,"TempoFlattened");
        CollectionAssert.AreEqual(new[]{60,72,60},choices[0].Melody.Notes.Select(n=>n.Pitch).ToArray());
        CollectionAssert.AreEqual(bytes,File.ReadAllBytes(path));
    }
    [TestMethod]
    public void AbcUsesBoundedDurationsTiesRestsAndBarlines() {
        var abc = MusicMelodyAbc.Build(new() { Notes = [new(61,60,60*70)] });
        StringAssert.Contains(abc,"L:1/32"); StringAssert.Contains(abc,"z1"); StringAssert.Contains(abc,"^C");
        StringAssert.Contains(abc,"- |"); Assert.IsFalse(abc.Contains("w:"));
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(abc.Split("V: Vocal\n")[1],@"[A-Ga-gz][,']*(\d+)"))
            CollectionAssert.Contains(new[]{1,2,3,4,6,8,12,16,24,32,48},int.Parse(m.Groups[1].Value));
    }
    [TestMethod, DataRow(MusicStudioRuntime.Variation), DataRow(MusicAceCatalog.Variation)]
    public async Task GenerationUsesFrozenMelodyAndResumeDoesNotPlanAgain(string variation) {
        using var f = new MusicProjectTests.Files(); var jobs = new MusicGenerationJobs(Path.Combine(f.Root,"jobs"));
        var expert = variation == MusicAceCatalog.Variation ? MusicAceCatalog.Defaults() : new MusicExpertSettings { Variation = variation };
        var job = jobs.Create(f.Root,Path.Combine(f.Root,"out"),"Melody",1,30,"simple song","la la",expert:expert);
        job = MusicMelodyPipeline.Capture(job,Tune()); jobs.Save(job);
        Assert.AreEqual(2,job.DurationSeconds); Assert.IsFalse(job.DurationAutomatic);
        var worker = new Worker(); var runner = new MusicGenerationRunner(jobs,worker);
        await runner.RunAsync(job.Id,default); await runner.RunAsync(job.Id,default);
        Assert.AreEqual(0,worker.Plans); Assert.AreEqual(1,worker.Synths);
        if (variation == MusicStudioRuntime.Variation) {
            Assert.AreEqual("melody",worker.Request!.EffectiveExpert.Cot); StringAssert.Contains(worker.Request.Abc!,"=C8"); Assert.IsNull(worker.Request.AudioTask);
        } else {
            var request = worker.Request!; Assert.AreEqual(MusicAudioTaskMode.Cover,request.AudioTask!.Mode);
            var ace = (Dictionary<string,object?>)MusicAceRequest.Build(request)["params"]!;
            Assert.AreEqual("cover",ace["task_type"]); Assert.IsNotNull(ace["src_audio"]); Assert.IsFalse(ace.ContainsKey("reference_audio"));
            var variant = jobs.Load(job.Id).Variants[0]; Assert.IsNotNull(variant.MelodyInputHash);
            File.AppendAllText(variant.MelodyInputPath!,"changed");
            await Assert.ThrowsAsync<InvalidDataException>(() => MusicMelodyPipeline.PrepareAsync(jobs,jobs.Load(job.Id),variant,request,_=>{},default));
        }
        var metadata = MusicSongMetadata.Create(jobs.Load(job.Id),jobs.Load(job.Id).Variants[0],0);
        StringAssert.Contains(metadata["LOPATA_MELODY"],"Bpm"); Assert.IsFalse(metadata["LOPATA_MELODY"].Contains(f.Root.Replace("\\","\\\\")));
    }
    [TestMethod]
    public void ConflictingSourcesAndUnsupportedModelsCannotStart() {
        Assert.Throws<InvalidDataException>(() => MusicMelodyPipeline.Validate(Tune(),new() {Enabled=true},MusicStudioRuntime.Variation));
        Assert.Throws<InvalidDataException>(() => MusicMelodyPipeline.Validate(Tune(),null,MusicHeartMuLaCatalog.Variation));
        MusicMelodyPipeline.Validate(new(),null,MusicHeartMuLaCatalog.Variation);
    }
    private sealed class Worker : IMusicYueWorker {
        public int Plans, Synths; public MusicYueRequest? Request;
        public event Action<string>? Log { add{} remove{} }
        public Task PlanAsync(string model,MusicYueRequest request,string requestPath,string planPath,CancellationToken token) { Plans++; throw new AssertFailedException("User melody must bypass planner."); }
        public Task SynthesizeAsync(string model,string decoder,MusicYueRequest request,string requestPath,string outputPath,CancellationToken token) {
            Synths++; Request = request; MusicMelodyWave.Write(outputPath,Tune(),token); return Task.CompletedTask;
        }
    }
}
