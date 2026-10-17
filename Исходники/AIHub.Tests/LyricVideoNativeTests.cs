using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.LyricVideo;
using AIHub.Models;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class LyricVideoNativeTests
{
    [TestMethod, TestCategory("Native")]
    public async Task ExternalWaveWithoutTagsOrTimingProducesStandaloneVisualizer()
    {
        if (Environment.GetEnvironmentVariable("LOPATA_VIDEO_EMPTY") != "1") Assert.Inconclusive("Opt-in external audio.");
        var root = Environment.GetEnvironmentVariable("LOPATA_VIDEO_EVIDENCE") ?? throw new InvalidOperationException();
        var source = Path.Combine(root, "external.wav");
        using (var writer = new NAudio.Wave.WaveFileWriter(source, new NAudio.Wave.WaveFormat(48000, 16, 2)))
        {
            var samples = new byte[48000 * 4 * 3];
            for (var i = 0; i < 48000 * 3; i++)
            {
                var value = (short)(Math.Sin(i * 2 * Math.PI * 300 / 48000) * 6000);
                System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(samples.AsSpan(i * 4), value);
                System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(samples.AsSpan(i * 4 + 2), value);
            }
            writer.Write(samples);
        }
        var hash = await ClipRuntime.HashAsync(source, default);
        var session = await ClipSession.LoadAsync(new(source, Path.Combine(root, "external-cache")), null, default);
        Assert.IsNull(session.Lyrics); Assert.IsNull(session.Cover); Assert.HasCount(0, session.Provenance.Models);
        await VideoExport.RunAsync(session, Path.Combine(root, "external-" + Guid.NewGuid().ToString("N") + ".mp4"), new() { Height = 720, SongWords = false },
            Guid.NewGuid().ToString("N"), 0, session.TotalDuration, _ => { }, null, default);
        Assert.AreEqual(hash, await ClipRuntime.HashAsync(source, default));
    }
    [TestMethod, TestCategory("Native")]
    public async Task RealSongExportsBothSizesWithInheritedTagsAndFullClock()
    {
        if (Environment.GetEnvironmentVariable("LOPATA_VIDEO_NATIVE") != "1") Assert.Inconclusive("Opt-in video export.");
        var source = Environment.GetEnvironmentVariable("LOPATA_VIDEO_SOURCE") ?? throw new InvalidOperationException();
        var output = Environment.GetEnvironmentVariable("LOPATA_VIDEO_EVIDENCE") ?? throw new InvalidOperationException();
        Directory.CreateDirectory(output); var hash = await ClipRuntime.HashAsync(source, default);
        var session = await ClipSession.LoadAsync(new(source, output), message => File.AppendAllText(Path.Combine(output, "native.log"), message + "\n"), default);
        Assert.IsNotNull(session.Lyrics); Assert.IsGreaterThan(30, session.Lyrics.Lines.Length);
        var receipt = Path.Combine(output, "cache", hash, "analysis.json"); var modified = File.GetLastWriteTimeUtc(receipt);
        var again = await ClipSession.LoadAsync(new(source, output), null, default);
        Assert.AreEqual(modified, File.GetLastWriteTimeUtc(receipt)); Assert.AreEqual(session.SourceHash, again.SourceHash);
        foreach (var (height, from, length) in new[] { (720, 70d, 4d), (1080, 0d, session.TotalDuration) })
        {
            var options = new ClipOptions { Height = height, ExtraRainText = "ЛОПАТА\nМузыка оживает", Palette = height == 720 ? "cyan" : "green" };
            var path = Path.Combine(output, "clip-" + height + ".mp4");
            var id = Guid.NewGuid().ToString("N");
            await Task.Run(() => VideoExport.RunAsync(session, path, options, id, from, length,
                message => File.AppendAllText(Path.Combine(output, "native.log"), message + "\n"), null, default));
            Assert.IsTrue(File.Exists(path));
            using var renderer = new SceneRenderer(session, options);
            File.WriteAllBytes(Path.Combine(output, "frame-" + height + ".png"), renderer.Png(30));
        }
        Assert.AreEqual(hash, await ClipRuntime.HashAsync(source, default));
        await File.WriteAllTextAsync(Path.Combine(output, "native-result.json"), JsonSerializer.Serialize(new {
            session.SourceHash, session.SongDuration, session.TotalDuration,
            SourceTags = session.SourceTags.Keys, Models = session.Provenance.Models,
            Outputs = new[] { "clip-720.mp4", "clip-1080.mp4" }, InputUnchanged = true
        }));
    }

    [TestMethod, TestCategory("Native")]
    public async Task PauseOtherWorkResumeReusesAnalysisAndPublishesOnce()
    {
        if (Environment.GetEnvironmentVariable("LOPATA_VIDEO_PAUSE") != "1") Assert.Inconclusive("Opt-in durable video pause.");
        var source = Environment.GetEnvironmentVariable("LOPATA_VIDEO_SOURCE") ?? throw new InvalidOperationException();
        var output = Environment.GetEnvironmentVariable("LOPATA_VIDEO_EVIDENCE") ?? throw new InvalidOperationException();
        Directory.CreateDirectory(output);
        var previous = ApplicationBackgroundOperations.Current;
        var controller = new BackgroundOperationController(new(Path.Combine(output, "background.json")));
        ApplicationBackgroundOperations.Current = controller;
        var work = new ClipWork();
        var request = new ClipRequest(Guid.NewGuid().ToString("N"), source, await ClipRuntime.HashAsync(source, default),
            Path.Combine(output, "resumed.mp4"), new() { Height = 720 }, "ru");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        work.Changed += progress => { if (progress.Progress > .01) started.TrySetResult(); };
        using var cancel = new CancellationTokenSource(); Task<string>? running = null;
        try
        {
            running = work.RunAsync(request, cancel.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(60));
            await controller.PauseAsync();
            Assert.AreEqual(BackgroundOperationPhase.Paused, controller.State!.Phase);
            var pausedId = controller.State.Id;
            Assert.IsFalse(controller.IsRunning);
            Assert.IsFalse(File.Exists(request.Destination));
            var analysisPath = Path.Combine(ClipWork.DirectoryPath, "runs", request.Id, "cache", request.SourceHash, "analysis.json");
            var analysisTime = File.GetLastWriteTimeUtc(analysisPath);
            var other = new BackgroundOperationState { Kind = "other", Title = "other", Input = JsonSerializer.SerializeToElement(new { }) };
            Assert.AreEqual(7, await controller.RunAsync(other, _ => Task.FromResult(7), () => Task.CompletedTask, default));
            await controller.ResumeAsync(pausedId, default);
            var result = await running.WaitAsync(TimeSpan.FromMinutes(5));
            Assert.AreEqual(request.Destination, result);
            Assert.IsTrue(File.Exists(result));
            Assert.IsFalse(File.Exists(result + ".lopata-" + request.Id + ".partial"));
            Assert.IsFalse(controller.IsRunning);
            Assert.AreEqual(analysisTime, File.GetLastWriteTimeUtc(analysisPath));
            var resultTime = File.GetLastWriteTimeUtc(result);
            await work.RunAsync(request, default);
            Assert.AreEqual(resultTime, File.GetLastWriteTimeUtc(result));
        }
        finally
        {
            cancel.Cancel(); if (running is not null) try { await running; } catch (OperationCanceledException) { }
            ApplicationBackgroundOperations.Current = previous;
        }
    }
}
