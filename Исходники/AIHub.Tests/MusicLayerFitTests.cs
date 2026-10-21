using System.Buffers.Binary;
using System.IO;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class MusicLayerFitTests
{
    [TestMethod]
    public void PartialPhraseSearchAllowsModerateDifferencesWithoutInventingMissingTimes()
    {
        const string words = "one\ntwo\nthree\nfour";
        var rows = MusicLyricsCtcProtocol.SourceRows(words);
        MusicLyricsAlignmentResult Timings(double shift, bool partial) => new(20, "test", "test", partial ? "partial" : "complete",
            rows.Take(partial ? 3 : 4).Select((r, i) => new MusicTimedLyricsLine(r.SourceStart, r.SourceLength, 1 + i * 4 + shift, 3 + i * 4 + shift)).ToArray(), "");
        var plan = MusicLayerFitPlan.Create(20, 19, words, words, Timings(0, true), Timings(-.5, false) with { DurationSeconds = 19 });
        Assert.AreEqual("phrase-wsola-v1", plan.Method); Assert.IsTrue(plan.Approximate); Assert.AreEqual(3, plan.MatchedPhrases);
        Assert.AreEqual(0, plan.Anchors[0].Original); Assert.AreEqual(20, plan.Anchors[^1].Original);
        Assert.IsFalse(plan.Anchors.Any(p => p.Original == 13), "An unrecognized fourth phrase must not become a measured anchor.");
        var fallback = MusicLayerFitPlan.Create(20, 19, words, words, null, Timings(0, false));
        Assert.AreEqual("duration-wsola-v1", fallback.Method); Assert.AreEqual(2, fallback.Anchors.Length);
        Assert.Throws<InvalidDataException>(() => MusicLayerFitPlan.Create(20, 4, words, words, null, null));
    }

    [TestMethod]
    public void StretchKeepsPitchAndStereoRelationshipAndNeverChangesInput()
    {
        using var files = new MusicProjectTests.Files(); Directory.CreateDirectory(files.Root);
        var input = Tone(files.Root, "tone", 2); var before = File.ReadAllBytes(input); var output = Path.Combine(files.Root, "fitted.wav");
        var plan = MusicLayerFitPlan.Create(2.4, 2, "a", "b", null, null);
        MusicPcmTimeFit.Write(input, output, plan, default);
        Assert.AreEqual(2.4, MusicWaveFile.ReadDuration(output).TotalSeconds, .00003);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(input));
        var pcm = MusicPcm16.Read(output); var crossings = 0; short previous = 0;
        for (var frame = 4800; frame < 110400; frame++) {
            var left = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(frame * 4, 2));
            var right = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(frame * 4 + 2, 2));
            if (previous <= 0 && left > 0) crossings++; previous = left;
            Assert.IsTrue(Math.Abs(right * 2 - left) <= 3, "Stereo channels must share the same timing decision.");
        }
        Assert.AreEqual(440, crossings / 2.2, 5, "Changing duration must not transpose the tone.");
    }

    [TestMethod]
    public void PhraseMapMovesAnAudibleEventInsteadOfOnlyPaddingTheEnd()
    {
        using var files = new MusicProjectTests.Files(); Directory.CreateDirectory(files.Root);
        var input = Tone(files.Root, "pulse", 8, t => t >= 2 && t < 2.4); var output = Path.Combine(files.Root, "moved.wav");
        var plan = new MusicLayerFitSummary("phrase-wsola-v1", true, 1, 1, 8, 8, 1,
            [new(0, 0), new(3, 2), new(4, 3), new(8, 8)]);
        MusicPcmTimeFit.Write(input, output, plan, default); var pcm = MusicPcm16.Read(output);
        Assert.IsTrue(Energy(2, 2.4) < 1, "The old phrase location must be silent.");
        Assert.IsTrue(Energy(3.05, 3.3) > 1000, "The phrase must move to the reference timeline.");
        double Energy(double start, double end) {
            var sum = 0d; var count = 0;
            for (var frame = (int)(start * 48000); frame < end * 48000; frame += 16) { var value = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(frame * 4, 2)); sum += value * (double)value; count++; }
            return Math.Sqrt(sum / count);
        }
    }

    [TestMethod]
    public void CancellationDoesNotLeaveAFittedFile()
    {
        using var files = new MusicProjectTests.Files(); Directory.CreateDirectory(files.Root);
        var input = Tone(files.Root, "cancel", 1); var output = Path.Combine(files.Root, "cancelled.wav");
        Assert.Throws<OperationCanceledException>(() => MusicPcmTimeFit.Write(input, output,
            MusicLayerFitPlan.Create(1.1, 1, "a", "b", null, null), new CancellationToken(true)));
        Assert.IsFalse(File.Exists(output));
    }

    [TestMethod]
    public async Task MissingAcousticEvidenceProducesAnExplicitApproximateResult()
    {
        using var files = new MusicProjectTests.Files(); Directory.CreateDirectory(files.Root);
        var original = Tone(files.Root, "original", 1.1); var fresh = Tone(files.Root, "fresh", 1);
        var fitter = new MusicAudioTaskAlignment((_, _, _, _) => throw new IOException("No acoustic coverage"));
        var result = await fitter.FitAsync(original, fresh, fresh, "a", "b", files.Root, _ => { }, default);
        Assert.AreEqual("duration-wsola-v1", result.Summary.Method); Assert.IsTrue(result.Summary.Approximate);
        Assert.AreEqual(1.1, MusicWaveFile.ReadDuration(result.Path).TotalSeconds, .00003);
        Assert.AreEqual(await MusicAudioTaskFiles.HashAsync(result.Path, default), result.Hash);
    }

    [TestMethod]
    public async Task ResumeAfterFitCheckpointReusesTheAdjustedLayerAndRetainsOriginalMusic()
    {
        using var files = new MusicProjectTests.Files(); var jobs = new MusicGenerationJobs(Path.Combine(files.Root, "jobs"));
        var job = jobs.Create(files.Root, Path.Combine(files.Root, "out"), "Fit checkpoint", 1, 1, "", "words", expert: MusicAceCatalog.Defaults());
        var folder = Path.Combine(jobs.Folder(job.Id), "audio-inputs"); Directory.CreateDirectory(folder);
        var original = Tone(folder, "original", 1.1); var fresh = Tone(folder, "fresh", 1);
        async Task<MusicStemReceipt> Stems(string path) {
            var hash = await MusicAudioTaskFiles.HashAsync(path, default);
            return new(path, path, hash, hash, hash, "test", "cpu", 1);
        }
        var originalHash = await MusicAudioTaskFiles.HashAsync(original, default);
        job = job with { AudioTask = new() { Enabled = true, Mode = MusicAudioTaskMode.ReplaceVoice, Source = new(original) { Sha256 = originalHash }, SourceLyrics = "words" } };
        var variant = job.Variants[0] with { AudioTask = new() { RenderPath = fresh, RenderHash = await MusicAudioTaskFiles.HashAsync(fresh, default),
            OriginalStems = await Stems(original), RenderedStems = await Stems(fresh) } };
        job = job with { Variants = [variant] }; jobs.Save(job);
        var calls = 0; var fitter = new MusicAudioTaskAlignment((_, _, _, _) => { calls++; throw new IOException("Fixture without acoustic evidence"); });
        var pipeline = new MusicAudioTaskPipeline(layerFitter: fitter);
        void Save(MusicGenerationVariant value) { job = job with { Variants = [value] }; jobs.Save(job); }
        await Assert.ThrowsAsync<IOException>(() => pipeline.FinishAsync(job, variant, value => { Save(value); throw new IOException("Interrupted after durable fitted layer"); }, default));
        var checkpoint = jobs.Load(job.Id).Variants[0]; var callsBefore = calls;
        Assert.IsNotNull(checkpoint.AudioTask!.FittedLayerHash);
        var done = await pipeline.FinishAsync(job, checkpoint, Save, default);
        Assert.AreEqual(callsBefore, calls, "Resume must not repeat the phrase search or fit.");
        Assert.AreEqual(originalHash, await MusicAudioTaskFiles.HashAsync(original, default));
        Assert.AreEqual(1.1, MusicWaveFile.ReadDuration(done.AudioFile!).TotalSeconds, .00003);
        var metadata = MusicSongMetadata.Create(job, done, 0);
        StringAssert.Contains(metadata["LOPATA_AUDIO_PROCESSING"], "duration-wsola-v1");
        Assert.IsFalse(metadata["LOPATA_AUDIO_PROCESSING"].Contains(files.Root));
    }

    private static string Tone(string root, string name, double seconds, Func<double, bool>? audible = null)
    {
        var pcm = new byte[(int)Math.Round(seconds * 48000) * 4];
        for (var frame = 0; frame < pcm.Length / 4; frame++) {
            var value = (short)(audible?.Invoke(frame / 48000d) != false ? Math.Round(10000 * Math.Sin(2 * Math.PI * 440 * frame / 48000)) : 0);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(frame * 4, 2), value); BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(frame * 4 + 2, 2), (short)(value / 2));
        }
        var path = Path.Combine(root, name + ".wav"); MusicPcm16.Write(path, pcm); return path;
    }
}
