using System.IO;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.VideoEditor;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoNarratorLongWorkTests
{
    [TestMethod][DataRow("regression")][DataRow("hour")][DataRow("resume")]
    public async Task RealSileroHandlesTheFailedScriptLongOutputAndResume(string mode)
    {
        var root = Environment.GetEnvironmentVariable("LOPATA_VOICE_LONG_EVIDENCE");
        var source = Environment.GetEnvironmentVariable("LOPATA_VOICE_LONG_REQUEST");
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(source))
        { Assert.Inconclusive("Explicit long synthesis invocation and saved request are required."); return; }
        var selected = Environment.GetEnvironmentVariable("LOPATA_VOICE_LONG_WORK_MODES");
        if (!string.IsNullOrEmpty(selected) && !selected.Split(',').Contains(mode))
        { Assert.Inconclusive("This controlled work mode was not selected."); return; }
        Directory.CreateDirectory(root);
        using var audit = ComponentAudit.UseTestStore(Environment.GetEnvironmentVariable("LOPATA_VOICE_LONG_AUDIT") ?? Path.Combine(root, "audit"), false);
        using var operation = ComponentAudit.CaptureOperation();
        var confirm = ComponentLicenseGate.ConfirmAsync; ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
        try
        {
            using var original = JsonDocument.Parse(await File.ReadAllTextAsync(source));
            var text = original.RootElement.GetProperty("text").GetString()!;
            Assert.IsGreaterThan(1000, text.Length);
            var repeats = 1;
            if (mode == "hour")
            {
                using var baseline = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "regression.json")));
                repeats = (int)Math.Ceiling(3600 / baseline.RootElement.GetProperty("seconds").GetDouble()) + 2;
                text = string.Join("\n\n", Enumerable.Repeat(text, repeats));
            }
            if (mode == "resume")
            {
                repeats = 16; text = string.Join("\n\n", Enumerable.Repeat(text, repeats));
            }
            var p = new VideoProject { Name = "Long narrator controlled check", DefaultVoice = "silero56", DefaultVoiceOptions = new("xenia"),
                Narration = [new() { End = mode == "hour" ? 3600 : 60, Text = text, Provider = "silero56", VoiceOptions = new("xenia") }] };
            var input = Path.Combine(root, mode + "-" + p.Id + "-input.lopataVideo"); VideoProjectStore.Save(p, input);
            var request = new VideoWorkRequest(Guid.NewGuid().ToString("N"), input, "speech", Voice: "silero56", Whole: true, VoiceOptions: p.DefaultVoiceOptions);
            var workDirectory = Path.Combine(VideoProjectStore.Work(p.Id), "Operations", request.Id);
            var packetDirectory = Path.Combine(workDirectory, "SpeechChunks", "whole");
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(25));
            var worker = new VideoWork(); var progress = new List<double>(); worker.Changed += value => { if (value.Status == "VideoEditor.Speaking") progress.Add(value.Fraction); };
            var completedBeforeResume = 0;
            if (mode == "resume")
            {
                using var cancel = new CancellationTokenSource();
                void StopAfterPacket(VideoWorkProgress value) { if (value.Status == "VideoEditor.Speaking" && value.Fraction > 0) cancel.Cancel(); }
                worker.Changed += StopAfterPacket;
                await Assert.ThrowsAsync<OperationCanceledException>(() => worker.RunAsync(request, cancel.Token));
                worker.Changed -= StopAfterPacket;
                Assert.IsFalse(File.Exists(Path.Combine(workDirectory, "whole.voice.json")), "An interrupted operation must not publish a completed receipt.");
                completedBeforeResume = Directory.EnumerateFiles(Path.Combine(packetDirectory, "chunks"), "*.json", SearchOption.AllDirectories).Count();
                Assert.IsGreaterThan(0, completedBeforeResume);
            }
            var result = VideoProjectStore.Load(await worker.RunAsync(request, deadline.Token));
            var audio = result.Assets.Single(a => a.NarratorProfile?.Provider == "silero56");
            using var wave = new NAudio.Wave.WaveFileReader(audio.Path);
            Assert.IsGreaterThan(mode == "hour" ? 3600 : 10, wave.TotalTime.TotalSeconds);
            using var execution = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(packetDirectory, "execution.json")));
            var chunking = execution.RootElement.GetProperty("actual").GetProperty("chunking");
            Assert.AreEqual(text.Length, chunking.GetProperty("inputChars").GetInt32());
            Assert.IsGreaterThan(1, chunking.GetProperty("packets").GetInt32());
            if (mode == "resume")
            {
                Assert.IsGreaterThanOrEqualTo(completedBeforeResume, chunking.GetProperty("reused").GetInt32());
                Assert.IsLessThan(chunking.GetProperty("packets").GetInt32(), completedBeforeResume,
                    "The cancellation control must interrupt before all packets are completed.");
            }
            Assert.IsTrue(progress.Any(f => f > 0 && f < 1));
            File.Copy(audio.Path, Path.Combine(root, mode + ".wav"), true);
            File.Copy(Path.Combine(packetDirectory, "chunks.jsonl"), Path.Combine(root, mode + "-chunks.jsonl"), true);
            await File.WriteAllTextAsync(Path.Combine(root, mode + ".json"), JsonSerializer.Serialize(new { mode, projectId = p.Id, operationId = request.Id,
                inputChars = text.Length, repeats, seconds = wave.TotalTime.TotalSeconds, completedBeforeResume, progress,
                audio.Models, execution = execution.RootElement.Clone() }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { ComponentLicenseGate.ConfirmAsync = confirm; }
    }
}
