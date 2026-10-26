using System.IO;
using System.Text.Json;
using AIHub.Controls.VideoEditor;
using AIHub.Services;
using AIHub.Services.VideoEditor;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoVoiceWholeInstalledTests
{
    [TestMethod]
    public async Task AppliedSileroChoiceGeneratesWholeAudioAndRecordedSummaryThroughRealVideoWork()
    {
        var evidence = Environment.GetEnvironmentVariable("LOPATA_VOICE_SELECTION_EVIDENCE");
        if (string.IsNullOrWhiteSpace(evidence)) { Assert.Inconclusive("Explicit real synthesis invocation required."); return; }
        Directory.CreateDirectory(evidence);
        using var audit = ComponentAudit.UseTestStore(Path.Combine(evidence, "audit"), false);
        using var operation = ComponentAudit.CaptureOperation();
        var gate = ComponentLicenseGate.ConfirmAsync; ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
        try
        {
            var project = VideoVoiceSelectionTests.TenFragments(); project.Name = "Voice selection regression check";
            VideoVoiceProfiles.Apply(project, new("silero56", "ru", 100, new("xenia")), false, new HashSet<string> { project.Narration[0].Id });
            var needed = VideoEditorWindow.ModelNeed(project, "speech", null, true);
            CollectionAssert.AreEqual(new[] { "silero56" }, needed.Voices);
            var input = Path.Combine(evidence, "input.lopataVideo"); VideoProjectStore.Save(project, input);
            var request = new VideoWorkRequest(Guid.NewGuid().ToString("N"), input, "speech", Whole: true,
                Voice: project.DefaultVoice, Language: project.DefaultLanguage, Rate: project.DefaultRate, VoiceOptions: project.DefaultVoiceOptions);
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
            var resultPath = await new VideoWork().RunAsync(request, deadline.Token);
            var result = VideoProjectStore.Load(resultPath);
            var audio = result.Assets.Single(a => a.NarratorProfile is not null);
            Assert.AreEqual("silero56", audio.NarratorProfile!.Provider);
            Assert.IsTrue(result.Narration.All(n => n.AudioAssetId == audio.Id && !n.Stale));
            Assert.IsTrue(result.Narration.Skip(1).All(n => n.Provider == "kokoro-sveta"));
            var summary = VideoVoiceSummary.Resolve(result, result.Narration.Select(n => n.Id).ToHashSet(), true, .1);
            Assert.HasCount(1, summary.Profiles); Assert.AreEqual("silero56", summary.Profiles[0]!.Provider);
            using var wave = new NAudio.Wave.WaveFileReader(audio.Path); Assert.IsGreaterThan(.1, wave.TotalTime.TotalSeconds);
            File.Copy(audio.Path, Path.Combine(evidence, "whole-silero.wav"), true);
            File.Copy(resultPath, Path.Combine(evidence, "result.lopataVideo"), true);
            await File.WriteAllTextAsync(Path.Combine(evidence, "real-work.json"), JsonSerializer.Serialize(new {
                project.Id, request, needed.Voices, audio.NarratorProfile, audio.Models, seconds = wave.TotalTime.TotalSeconds,
                profilesInFragments = result.Narration.Select(n => n.Provider).ToArray(), actualSummary = summary.Profiles
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { ComponentLicenseGate.ConfirmAsync = gate; }
    }
}
