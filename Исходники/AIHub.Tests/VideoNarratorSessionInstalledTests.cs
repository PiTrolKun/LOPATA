using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.VideoEditor;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoNarratorSessionInstalledTests
{
    [TestMethod]
    public async Task RealGpuSessionReusesItsModelAndCancellationReleasesItsProcess()
    {
        var library = Environment.GetEnvironmentVariable("LOPATA_VOICE_SESSION_LIBRARY");
        var evidence = Environment.GetEnvironmentVariable("LOPATA_VOICE_GPU_OUTPUT");
        if (string.IsNullOrWhiteSpace(library) || string.IsNullOrWhiteSpace(evidence))
        { Assert.Inconclusive("Explicit installed GPU session check required."); return; }
        using var audit = ComponentAudit.UseTestStore(Path.Combine(evidence, "audit-session"), false);
        using var operation = ComponentAudit.CaptureOperation();
        var confirm = ComponentLicenseGate.ConfirmAsync; ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
        try
        {
            using var narrator = new VideoNarratorRegistry(new ManagedModelLibraryStore(library)).Create("kokoro-heart");
            var configured = (IConfiguredVideoNarrator)narrator;
            var options = new VideoVoiceOptions("af_heart", Device: "gpu");
            var first = await configured.GenerateAsync("kokoro-heart", "en", "Hello world.", 100, options, default);
            using var initial = Read(first);
            var pid = initial.RootElement.GetProperty("pid").GetInt32();
            Assert.IsTrue(initial.RootElement.GetProperty("isGpu").GetBoolean());
            var second = await configured.GenerateAsync("kokoro-heart", "en", "This second sentence checks that the current request replaces the first one.", 100, options, default);
            using var reused = Read(second);
            Assert.AreEqual(pid, reused.RootElement.GetProperty("pid").GetInt32());
            Assert.AreEqual(0.0, reused.RootElement.GetProperty("loadSeconds").GetDouble());
            using var wave1 = new NAudio.Wave.WaveFileReader(first.Path);
            using var wave2 = new NAudio.Wave.WaveFileReader(second.Path);
            Assert.IsGreaterThan(wave1.TotalTime.TotalSeconds, wave2.TotalTime.TotalSeconds);
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            await Assert.ThrowsAsync<OperationCanceledException>(() => configured.GenerateAsync("kokoro-heart", "en",
                string.Join(' ', Enumerable.Repeat("This is a longer narration to check cancellation.", 100)), 100, options, cancel.Token));
            Assert.IsFalse(Process.GetProcesses().Any(p => { using (p) return p.Id == pid; }), "Cancellation must release the owned model process.");
            await File.WriteAllTextAsync(Path.Combine(evidence, "session-reuse-cancellation.json"), JsonSerializer.Serialize(new {
                pid, initial = initial.RootElement.Clone(), reused = reused.RootElement.Clone(), cancelled = true, processReleased = true
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { ComponentLicenseGate.ConfirmAsync = confirm; }
    }
    private static JsonDocument Read(NarratorAudio audio) => JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(audio.Path)!, "execution.json")));
}
