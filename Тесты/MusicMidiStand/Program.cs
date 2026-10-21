using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.MusicMidi;

var mode = args[0]; var root = Path.GetFullPath(args[1]); var folder = Path.GetFullPath(args[2]); Directory.CreateDirectory(folder);
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(12)); var token = deadline.Token;
var pitches = new[] {60,64,67,64,62,65,69,67,64,67,72,71,69,67,64,60};
var melody = new MusicMelody { Enabled = true, Bpm = 100, Notes = pitches.Select((p,i)=>new MusicMelodyNote(p,i*480,480)).ToArray() };
MusicMidiFile.Write(Path.Combine(folder,"melody.mid"),melody);
MusicMidiFile.SaveProject(Path.Combine(folder,"melody.lopata-melody.json"),melody);
MusicMelodyWave.Write(Path.Combine(folder,"preview.wav"),melody,token);
if (mode == "export") return;
var studio = mode == "studio"; var expert = MusicModelVariants.Defaults(studio?MusicStudioRuntime.Variation:MusicAceCatalog.Variation);
if(studio) expert.Values["steps"]=8; else expert.Values["thinking"]=0;
var jobs = new MusicGenerationJobs(Path.Combine(folder,"jobs"));
var job = jobs.Create(root,Path.Combine(folder,"results"),"MIDI physical "+mode,1,30,"warm simple folk song, clear solo voice, acoustic guitar",
    "[Verse]\nSoft light across the sky\nA little tune goes by\nWe follow where it flows\nAnd sing until it slows",expert:expert,outputSettings:new(){Format=MusicAudioFormat.Wav});
job = MusicMelodyPipeline.Capture(job,melody); jobs.Save(job);
IMusicYueWorker worker = studio ? new MusicStudioWorker() : new MusicAceWorker(); worker.Log += Console.WriteLine;
await new MusicGenerationRunner(jobs,worker,log:Console.WriteLine).RunAsync(job.Id,token);
await File.WriteAllTextAsync(Path.Combine(folder,"physical-result.json"),JsonSerializer.Serialize(jobs.Load(job.Id),new JsonSerializerOptions{WriteIndented=true}),token);
Console.WriteLine("MIDI physical completed: "+job.Id);
