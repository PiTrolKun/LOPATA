using System.IO;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class MusicAudioInputPolicyTests
{
    [TestMethod]
    public void StudioCoverBlocksReferencesWithoutDestroyingProjectChoices()
    {
        var task = new MusicAudioTask { Enabled = true, Mode = MusicAudioTaskMode.Cover,
            Source = new(Path.GetFullPath("source.wav")), References = [new(Path.GetFullPath("reference.wav"))] };
        task.Validate(MusicStudioRuntime.Variation);
        Assert.AreEqual(0, MusicAudioInputPolicy.ActiveReferences(task, MusicStudioRuntime.Variation).Count());
        Assert.IsTrue(task.References[0].Enabled);
        Assert.AreEqual(1, MusicAudioInputPolicy.ActiveReferences(task with { Mode = MusicAudioTaskMode.NewSong }, MusicStudioRuntime.Variation).Count());
        foreach (var mode in Enum.GetValues<MusicAudioTaskMode>()) {
            Assert.AreEqual(mode != MusicAudioTaskMode.Restore, MusicAudioInputPolicy.AllowsReferences(MusicAceCatalog.Variation, mode));
            Assert.AreEqual(mode == MusicAudioTaskMode.NewSong, MusicAudioInputPolicy.AllowsReferences(MusicStudioRuntime.Variation, mode));
            Assert.IsFalse(MusicAudioInputPolicy.AllowsReferences(MusicHeartMuLaCatalog.Variation, mode));
        }
    }

    [TestMethod]
    public void OldContradictoryStagedJobIsRejectedInsteadOfSilentlyResumingIt()
    {
        var input = new MusicAudioInput(Path.GetFullPath("source.wav")) { Sha256 = new('A', 64) };
        var task = new MusicAudioTask { Enabled = true, Mode = MusicAudioTaskMode.Cover, Source = input, References = [input] };
        task.Validate(MusicStudioRuntime.Variation, true); // History remains readable.
        var error = Assert.Throws<InvalidDataException>(() => MusicAudioInputPolicy.ValidateExecution(task, MusicStudioRuntime.Variation));
        Assert.AreEqual("Music.Transform.UnsupportedStagedReferences", error.Message);
        task.Validate(MusicAceCatalog.Variation, true);
        (task with { References = [] }).Validate(MusicStudioRuntime.Variation, true);
        MusicAudioInputPolicy.ValidateExecution(task with { References = [] }, MusicStudioRuntime.Variation);
    }

    [TestMethod]
    public void StudioConflictingReferenceFactsAreNotForwardedTogether()
    {
        var first = MusicStudioReferenceFacts.Read("X:1\nQ:1/4=78\nK:Dm\nM:2/4\n");
        Assert.AreEqual("78 BPM, Dm, 2/4", MusicStudioReferenceFacts.Combine([first, first]));
        foreach (var other in new[] { first with { Tempo = "179" }, first with { Meter = "4/4" }, first with { Key = "C" } }) {
            var error = Assert.Throws<InvalidDataException>(() => MusicStudioReferenceFacts.Combine([first, other]));
            Assert.AreEqual("Music.Transform.ConflictingStudioReferences", error.Message);
        }
        Assert.AreEqual("78 BPM, Dm, 2/4", MusicStudioReferenceFacts.Combine([first, MusicStudioReferenceFacts.Read("K:Dmin\n")]));
        Assert.Throws<InvalidDataException>(() => MusicStudioReferenceFacts.Read("X:1\nCDEF|"));
    }

    [TestMethod]
    public async Task UnsafeOldCoverRemainsReadableButCannotReachAudioAnalysis()
    {
        using var files = new MusicProjectTests.Files();
        var jobs = new MusicGenerationJobs(Path.Combine(files.Root, "jobs"));
        var job = jobs.Create(files.Root, Path.Combine(files.Root, "out"), "Old cover", 1, 9, "folk", "Words",
            expert: MusicModelVariants.Defaults(MusicStudioRuntime.Variation));
        var input = new MusicAudioInput(Path.Combine(jobs.Folder(job.Id), "audio-inputs", "not-opened.wav")) { Sha256 = new('A', 64) };
        job = job with { AudioTask = new() { Enabled = true, Mode = MusicAudioTaskMode.Cover, Source = input, References = [input] } };
        jobs.Save(job); Assert.IsNotNull(jobs.Load(job.Id).AudioTask);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new MusicAudioTaskPipeline().PrepareAsync(job, job.Variants[0],
            new("folk", "Words", 1, 2, 9), _ => Assert.Fail("No stage should be saved"), default));
        Assert.AreEqual("Music.Transform.UnsupportedStagedReferences", error.Message);
    }

    [TestMethod, DataRow(false), DataRow(true)]
    public async Task StudioPipelineSendsOnlyConsistentPositiveHints(bool conflicting)
    {
        using var files = new MusicProjectTests.Files();
        var jobs = new MusicGenerationJobs(Path.Combine(files.Root, "jobs"));
        var job = jobs.Create(files.Root, Path.Combine(files.Root, "out"), "New song", 1, 9, "folk", "Words",
            expert: MusicModelVariants.Defaults(MusicStudioRuntime.Variation));
        var folder = Path.Combine(jobs.Folder(job.Id), "audio-inputs");
        var references = new List<MusicAudioInput>();
        foreach (var (name, level) in new[] { ("a", (short)200), ("b", (short)300) }) {
            var path = MusicAudioTaskTests.Wave(folder, name, level);
            references.Add(new(path) { Sha256 = await MusicAudioTaskFiles.HashAsync(path, default) });
        }
        job = job with { AudioTask = new() { Enabled = true, Mode = MusicAudioTaskMode.NewSong, References = references.ToArray() } };
        var pipeline = new MusicAudioTaskPipeline(new Score(conflicting));
        Task<MusicYueRequest> Prepare() => pipeline.PrepareAsync(job, job.Variants[0], new("folk", "Words", 1, 2, 9), _ => { }, default);
        if (conflicting) {
            var error = await Assert.ThrowsAsync<InvalidDataException>(Prepare);
            Assert.AreEqual("Music.Transform.ConflictingStudioReferences", error.Message);
        } else {
            var request = await Prepare();
            Assert.AreEqual("folk; musical reference characteristics: 78 BPM, Dm, 2/4", request.Style);
            Assert.AreEqual("Words", request.Lyrics);
            Assert.IsTrue(string.IsNullOrEmpty(request.Abc));
        }
    }

    private sealed class Score(bool conflicting) : IMusicCoverScoreWorker
    {
        public Task<string> TranscribeAsync(string root, string audio, bool harmony, Action<string> log, CancellationToken token) =>
            Task.FromResult("X:1\nQ:" + (conflicting && Path.GetFileName(audio) == "b.wav" ? "179" : "78") + "\nK:Dm\nM:2/4\nCDEF|\n");
    }
}
