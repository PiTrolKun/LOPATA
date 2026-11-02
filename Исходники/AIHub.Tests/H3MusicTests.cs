using System.IO;
using System.Text;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.SongClip;
using AIHub.Services.VideoPreparation;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class H3MusicTests
{
    [TestMethod]
    public void MusicCardsShareExistingH3FilesAndPinEveryAddedSourceFile()
    {
        var root = Path.GetTempPath(); var cards = H3MusicComponents.Cards(root);
        foreach (var original in VideoH3Catalog.Cards(root).Append(VideoH3RuntimeCatalog.Card(root)))
        {
            var shared = cards.Single(c => c.ModelArtifactId == original.ModelArtifactId);
            Assert.AreEqual(original.InstallDirectory, shared.InstallDirectory);
            Assert.AreEqual(original.Revision, shared.Revision);
            CollectionAssert.AreEqual(original.Files.Select(f => f.Sha256).ToArray(), shared.Files.Select(f => f.Sha256).ToArray());
        }
        var source = cards.Single(c => c.ModelArtifactId == H3MusicComponents.SourceId);
        Assert.AreEqual(H3MusicComponents.SourceRevision, source.Revision);
        Assert.AreEqual("GPL-3.0-only", source.License);
        Assert.IsTrue(source.Files.Any(f => f.RelativePath == "LICENSE"));
        Assert.IsTrue(source.Files.Any(f => f.RelativePath == "example_workflows/NEW - Music Video.json"));
        Assert.AreEqual(source.Files.Count, H3MusicComponents.SourceTree.Length);
        foreach (var file in source.Files)
        {
            Assert.IsTrue(file.SourceUrl.Contains(source.Revision, StringComparison.Ordinal));
            Assert.AreEqual(64, file.Sha256.Length);
            Assert.IsTrue(file.Sha256.All(Uri.IsHexDigit));
            var entry = H3MusicComponents.SourceTree.Single(t => t.Path == file.RelativePath);
            Assert.AreEqual(file.SizeBytes, entry.Size); Assert.AreEqual(file.Sha256, entry.Sha256);
        }
        Assert.AreEqual(cards.Sum(c => c.TotalBytes), H3MusicComponents.TotalBytes);
        Assert.AreEqual(VideoH3Catalog.Cards(root).Count + 2, cards.Count);
    }

    [TestMethod]
    public void AddedSourceHasBundledOriginalLicenseWithoutAutoAcceptingIt()
    {
        using var files = new VideoPreparationTests.TestFiles();
        var service = new ComponentLicenseService(Path.Combine(AppContext.BaseDirectory, "Licenses"), files.File("receipts.json"));
        var entry = service.Entries.Single(e => e.Id == H3MusicComponents.SourceId);
        Assert.IsTrue(entry.Texts.Any(t => service.ReadText(t).Contains("GNU GENERAL PUBLIC LICENSE", StringComparison.Ordinal)));
        Assert.AreEqual(0, service.ReadReceipts().Count);
    }

    [TestMethod]
    [DataRow("frames")]
    [DataRow("context")]
    [DataRow("width")]
    [DataRow("fps")]
    [DataRow("model")]
    [DataRow("prompt")]
    public void InvalidMotionRequestCannotReachTheWorker(string field)
    {
        var attempt = Attempt();
        if (field == "frames") attempt.Settings.Motion.RawFrames = 240;
        if (field == "context") attempt.Settings.Motion.ContextFrames = 38;
        if (field == "width") attempt.Settings.Motion.Width = 610;
        if (field == "fps") attempt.Settings.Fps = 30;
        if (field == "model") attempt = attempt with { Model = "glitchframe" };
        if (field == "prompt") attempt.Settings.Prompt = " ";
        Assert.ThrowsExactly<InvalidDataException>(() => H3MusicRunner.ValidateSettings(attempt));
    }

    [TestMethod]
    public void CompleteSongAndFactualProvenanceAreAccepted()
    {
        var attempt = Attempt(); var expected = H3MusicRunner.Facts(H3MusicComponents.Cards(Path.GetTempPath()));
        Assert.AreEqual(6, H3MusicResultValidation.Validate(Output(attempt, expected), Source(), attempt.Settings, attempt.Id, expected));
    }

    [TestMethod]
    [DataRow("video-tail")]
    [DataRow("audio-tail")]
    [DataRow("revision")]
    [DataRow("operation")]
    [DataRow("dimensions")]
    [DataRow("fps")]
    public void PartialOrMisattributedResultIsRejected(string problem)
    {
        var attempt = Attempt(); var expected = H3MusicRunner.Facts(H3MusicComponents.Cards(Path.GetTempPath()));
        var output = Output(attempt, expected, problem);
        Assert.ThrowsExactly<InvalidDataException>(() => H3MusicResultValidation.Validate(output, Source(), attempt.Settings, attempt.Id, expected));
    }

    [TestMethod]
    public async Task HostBindingReusesOnlyTheSameImmutableAttempt()
    {
        using var files = new VideoPreparationTests.TestFiles();
        var store = new SongClipStore(files.File("projects")); var attempt = Attempt();
        WriteWave(store.Resolve(attempt.ProjectId, attempt.Song));
        var job = store.Resolve(attempt.ProjectId, "attempts/" + attempt.Id);
        var cards = H3MusicComponents.Cards(attempt.ModelsRoot);
        await H3MusicAttemptBinding.EnsureAsync(attempt, store, job, cards, CancellationToken.None);
        var binding = Path.Combine(job, "input-binding.json"); var original = File.ReadAllBytes(binding);
        await H3MusicAttemptBinding.EnsureAsync(attempt, store, job, cards, CancellationToken.None);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(binding));
    }

    [TestMethod]
    [DataRow("song")]
    [DataRow("reference")]
    [DataRow("settings")]
    [DataRow("lyrics")]
    [DataRow("pins")]
    [DataRow("binding")]
    public async Task ChangedInputsCannotAdoptAnAlreadyRenderedWorkerReceipt(string change)
    {
        using var files = new VideoPreparationTests.TestFiles();
        var store = new SongClipStore(files.File("projects")); var attempt = Attempt() with { Reference = "attachments/reference.png" };
        WriteWave(store.Resolve(attempt.ProjectId, attempt.Song));
        File.WriteAllBytes(store.Resolve(attempt.ProjectId, attempt.Reference), [1, 2, 3]);
        var job = store.Resolve(attempt.ProjectId, "attempts/" + attempt.Id); var cards = H3MusicComponents.Cards(attempt.ModelsRoot);
        await H3MusicAttemptBinding.EnsureAsync(attempt, store, job, cards, CancellationToken.None);
        var raw = Path.Combine(job, "motion-raw.mp4"); File.WriteAllBytes(raw, [7, 8, 9]);
        File.WriteAllText(Path.Combine(job, "worker-result.json"), "{\"output\":\"motion-raw.mp4\"}");
        if (change == "song") File.AppendAllText(store.Resolve(attempt.ProjectId, attempt.Song), "changed");
        if (change == "reference") File.AppendAllText(store.Resolve(attempt.ProjectId, attempt.Reference), "changed");
        if (change == "settings") attempt.Settings.Motion.Steps++;
        if (change == "lyrics") attempt = attempt with { Lyrics = "Other lyrics" };
        if (change == "pins") cards[0].Revision = new string('a', 40);
        if (change == "binding") File.WriteAllText(Path.Combine(job, "input-binding.json"), "{\"Schema\":1,\"InputHash\":\"changed\"}");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => H3MusicAttemptBinding.EnsureAsync(attempt, store, job, cards, CancellationToken.None));
        CollectionAssert.AreEqual(new byte[] { 7, 8, 9 }, File.ReadAllBytes(raw));
    }

    [TestMethod]
    public async Task LegacyReceiptWithoutBindingIsPreservedAndRejected()
    {
        using var files = new VideoPreparationTests.TestFiles();
        var store = new SongClipStore(files.File("projects")); var attempt = Attempt();
        WriteWave(store.Resolve(attempt.ProjectId, attempt.Song));
        var job = store.Resolve(attempt.ProjectId, "attempts/" + attempt.Id); Directory.CreateDirectory(job);
        var receipt = Path.Combine(job, "worker-result.json"); File.WriteAllText(receipt, "{}");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => H3MusicAttemptBinding.EnsureAsync(attempt, store, job,
            H3MusicComponents.Cards(attempt.ModelsRoot), CancellationToken.None));
        Assert.AreEqual("{}", File.ReadAllText(receipt));
        Assert.IsFalse(File.Exists(Path.Combine(job, "input-binding.json")));
    }

    [TestMethod]
    [TestCategory("InstalledRuntime")]
    public async Task InstalledH3MusicGeneratesLinkedChunksAndReusesTheFinalClip()
    {
        if (Environment.GetEnvironmentVariable("LOPATA_H3_MUSIC_INTEGRATION") != "1")
        { Assert.Inconclusive("Explicit installed H3 music runtime opt-in required."); return; }
        var root = Environment.GetEnvironmentVariable("LOPATA_H3_MUSIC_MODELS_ROOT");
        Assert.IsFalse(string.IsNullOrWhiteSpace(root), "The installed model root must be explicitly supplied.");
        var output = Environment.GetEnvironmentVariable("LOPATA_H3_MUSIC_TEST_ROOT")
            ?? Path.Combine(Path.GetTempPath(), "lopata-h3-music-" + Guid.NewGuid().ToString("N"));
        var store = new SongClipStore(Path.Combine(output, "projects"));
        var project = new SongClipProject { Name = "Own H3 music connected-chunk mini test", Model = H3MusicRunner.ModelId, Song = "attachments/source.wav" };
        WriteWave(store.Resolve(project.Id, project.Song)); project.Reference = "attachments/reference.png";
        using (var bitmap = new System.Drawing.Bitmap(64, 64))
        {
            using var drawing = System.Drawing.Graphics.FromImage(bitmap);
            drawing.Clear(System.Drawing.Color.CornflowerBlue);
            drawing.FillEllipse(System.Drawing.Brushes.ForestGreen, 12, 12, 40, 40);
            bitmap.Save(store.Resolve(project.Id, project.Reference), System.Drawing.Imaging.ImageFormat.Png);
        }
        store.Save(project);
        var attempt = new SongClipAttempt(Guid.NewGuid().ToString("N"), project.Id, project.Song, project.Reference, project.Name,
            "", "", new SongClipSettings { Width = 640, Height = 360, Fps = 24, Seed = 1729,
                Prompt = "A cinematic continuous shot of a peaceful sunlit woodland stream. Water flows gently past mossy stones, leaves move softly in a light breeze, consistent natural lighting, stable camera, no text.",
                Motion = new() { Width = 256, Height = 256, RawFrames = 124, ContextFrames = 39, Steps = 20 } }, root!, 8) { Model = H3MusicRunner.ModelId };
        SongClipStore.Atomic(store.Resolve(project.Id, "attempts/" + attempt.Id + "/snapshot.json"), attempt);
        SongClipStore.Atomic(Path.Combine(output, "fixture.json"), new { projectId = project.Id, attemptId = attempt.Id });
        var log = Path.Combine(output, "physical-mini.log");
        // Acquire only the newly introduced small source pack through the same
        // segmented service as the application. Existing H3 weights are reused.
        using (var preflight = new CancellationTokenSource(TimeSpan.FromMinutes(10)))
        {
            var source = H3MusicComponents.Cards(root!).Single(c => c.ModelArtifactId == H3MusicComponents.SourceId);
            await ComponentLicenseGate.EnsureAsync([source.ModelArtifactId], preflight.Token);
            var library = new ManagedModelLibraryStore(); library.Upsert(source);
            using var acquisition = new ManagedModelAcquisitionService(library) { MaximumParallelConnections = 8 };
            if ((await acquisition.VerifyAsync(source.ModelArtifactId, null, preflight.Token)).Status != ManagedModelStatuses.Installed)
            {
                var downloaded = await acquisition.DownloadAsync(source.ModelArtifactId, null, preflight.Token);
                Assert.AreEqual(ManagedModelStatuses.Installed, downloaded.Status, "The small music source pack must be ready before the timed physical run.");
            }
        }
        using var token = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        var chunks = store.Resolve(project.Id, "attempts/" + attempt.Id + "/motion-chunks");
        using (var stopAfterFirst = CancellationTokenSource.CreateLinkedTokenSource(token.Token))
        {
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => H3MusicRunner.RunAsync(attempt, store, (fraction, message) =>
            {
                File.AppendAllText(log, $"{DateTimeOffset.UtcNow:O} {fraction:F4} {message}\n");
                if (message.StartsWith('{'))
                {
                    using var e = JsonDocument.Parse(message);
                    if (e.RootElement.TryGetProperty("kind", out var kind) && kind.GetString() == "chunk"
                        && e.RootElement.GetProperty("index").GetInt32() == 1) stopAfterFirst.Cancel();
                }
            }, stopAfterFirst.Token));
        }
        Assert.AreEqual(1, Directory.GetFiles(chunks, "*.json").Length,
            "The first physical run must stop after its first committed chunk.");
        var firstFile = Path.Combine(chunks, "000001.safetensors");
        var firstModified = File.GetLastWriteTimeUtc(firstFile);
        var firstHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(firstFile)));
        var result = await H3MusicRunner.RunAsync(attempt, store, (fraction, message) =>
            File.AppendAllText(log, $"{DateTimeOffset.UtcNow:O} {fraction:F4} {message}\n"), token.Token);
        Assert.AreEqual(H3MusicRunner.ModelId, result.Model);
        var final = store.Resolve(project.Id, result.File); var modified = File.GetLastWriteTimeUtc(final);
        using var receipt = JsonDocument.Parse(File.ReadAllText(store.Resolve(project.Id, "attempts/" + attempt.Id + "/worker-result.json")));
        Assert.IsTrue(receipt.RootElement.GetProperty("completedChunks").GetInt32() >= 2);
        Assert.AreEqual(firstModified, File.GetLastWriteTimeUtc(firstFile), "Resume must not regenerate the completed first chunk.");
        Assert.AreEqual(firstHash, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(firstFile))));
        using var first = JsonDocument.Parse(File.ReadAllText(Path.Combine(chunks, "000001.json")));
        using var second = JsonDocument.Parse(File.ReadAllText(Path.Combine(chunks, "000002.json")));
        Assert.AreEqual(first.RootElement.GetProperty("data").GetProperty("sha256").GetString(),
            second.RootElement.GetProperty("identity").GetProperty("previousHash").GetString());
        Assert.IsTrue(second.RootElement.GetProperty("identity").GetProperty("contextFrames").GetInt32() > 0);
        var reused = await H3MusicRunner.RunAsync(attempt, store, (_, _) => Assert.Fail("A valid finished result must not start another model process."), token.Token);
        Assert.AreEqual(result.File, reused.File); Assert.AreEqual(modified, File.GetLastWriteTimeUtc(final));
        File.WriteAllText(Path.Combine(output, "result-path.txt"), final);
    }

    private static SongClipAttempt Attempt() => new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
        "attachments/song.wav", "", "fixture", "", "", new(), Path.GetTempPath(), 8) { Model = H3MusicRunner.ModelId };

    private static JsonElement Source() => JsonSerializer.SerializeToElement(new
    { streams = new[] { new { codec_type = "audio", duration = "6.000000" } }, format = new { duration = "6.000000" } });

    private static JsonElement Output(SongClipAttempt attempt, OutputModelUse[] expected, string problem = "")
    {
        var models = expected.Select(m => problem == "revision" ? m with { Revision = "unrelated" } : m).ToArray();
        var facts = new OutputProvenance { Scenario = "song-to-clip/h3_music_context", GenerationSeconds = 42, Models = models };
        return JsonSerializer.SerializeToElement(new
        {
            streams = new object[]
            {
                new { codec_type = "video", duration = problem == "video-tail" ? "4.000000" : "6.000000",
                    width = problem == "dimensions" ? 32 : attempt.Settings.Width, height = attempt.Settings.Height,
                    avg_frame_rate = problem == "fps" ? "30/1" : "24/1" },
                new { codec_type = "audio", duration = problem == "audio-tail" ? "3.000000" : "6.000000" }
            },
            format = new { duration = "6.000000", tags = new Dictionary<string, string>
                { ["LOPATA_VIDEO"] = facts.Json(), ["LOPATA_OPERATION"] = problem == "operation" ? "other" : attempt.Id } }
        });
    }

    private static void WriteWave(string path)
    {
        const int rate = 24000, seconds = 6; var count = rate * seconds;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = new BinaryWriter(File.Create(path), Encoding.ASCII);
        file.Write(Encoding.ASCII.GetBytes("RIFF")); file.Write(36 + count * 2);
        file.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); file.Write(16);
        file.Write((short)1); file.Write((short)1); file.Write(rate); file.Write(rate * 2);
        file.Write((short)2); file.Write((short)16); file.Write(Encoding.ASCII.GetBytes("data")); file.Write(count * 2);
        for (var index = 0; index < count; index++) file.Write((short)(Math.Sin(index * 2 * Math.PI * 220 / rate) * 2000));
    }
}
