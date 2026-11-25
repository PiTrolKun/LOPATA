using System.IO;
using System.IO.Compression;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.ExternalAgents;
using AIHub.Services.VideoConverter;
using AIHub.Services.VideoUpscale;
using Microsoft.Win32;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoConverterTests
{
    [TestMethod]
    public void PercentageAndResolutionAreIndependentAndLossesRequirePermission()
    {
        var video = new ConversionTrack(0, "video", "h264", 12_000_000, 0, 0, 10, 0, null, null, "");
        var audio = new ConversionTrack(1, "audio", "aac", 256_000, 2, 48000, 10, 0, "rus", null, "");
        var source = new ConversionMedia(15_000_000, 10, 1080, 1920, 0, "1:1", 60, "yuv420p", "mp4",
            video, [video, audio], new Dictionary<string, string>(), 0, false, null, null);
        var options = new ConversionOptions { Reduction = 50 };
        var unchanged = ConversionPlan.Create(options, source);
        Assert.AreEqual((1080, 1920, 0, 7_500_000L), (unchanged.Width, unchanged.Height, unchanged.Fps, unchanged.Limit!.Value));
        Assert.IsTrue(unchanged.Lossless);
        Assert.AreEqual("copy", unchanged.Tracks.Single().Encoder);
        var changed = ConversionPlan.Create(options with { ShortEdge = 720, PictureMayChange = true, AudioMayChange = true, MotionMayChange = true }, source);
        Assert.AreEqual((720, 1280, 30, 7_500_000L), (changed.Width, changed.Height, changed.Fps, changed.Limit!.Value));
        Assert.AreEqual("aac", changed.Tracks.Single().Encoder);
        Assert.IsNotNull(changed.Estimate);
        Assert.AreEqual(CompressionLevel.Optimal, ConversionBatch.ZipLevel(options));
        Assert.AreEqual(CompressionLevel.SmallestSize, ConversionBatch.ZipLevel(options with { Reduction = 85 }));
    }

    [TestMethod]
    public void ConverterShellUsesLiteralPathsAndCannotRemoveAnotherInstall()
    {
        using var fixture = new VideoPreparationTests.TestFiles();
        var file = fixture.File("Клип с пробелами.mp4");
        var another = fixture.File("Видео 2.MOV");
        var request = VideoConverterRequest.Parse(["--shell-video-convert", "--", file, another, file]);
        CollectionAssert.AreEqual(new[] { file, another }, request!.Paths.ToArray());
        Assert.ThrowsExactly<InvalidDataException>(() => VideoConverterRequest.Parse(["--shell-video-convert", "--", fixture.File("not.exe")]));
        var branch = @"Software\AIHub.Tests\" + Guid.NewGuid().ToString("N");
        var executable = fixture.File("AIHub.exe");
        var foreign = fixture.File("Second.exe");
        File.WriteAllText(executable, "test");
        var shell = MediaShellIntegration.Video(executable, branch);
        try
        {
            shell.Apply(true, "ЛОПАТА", "Перекодировать видео…");
            using (var key = Registry.CurrentUser.OpenSubKey(shell.RegisteredPath + @"\shell\01-convert\command"))
                Assert.AreEqual($"\"{executable}\" --shell-video-convert -- \"%1\"", key?.GetValue(""));
            MediaShellIntegration.Video(foreign, branch).Apply(false, "", "");
            using (var retained = Registry.CurrentUser.OpenSubKey(shell.RegisteredPath))
                Assert.AreEqual(MediaShellIntegration.VideoMarker, retained?.GetValue("LOPATA.Owner"));
            shell.Apply(false, "", "");
            Assert.IsNull(Registry.CurrentUser.OpenSubKey(shell.RegisteredPath));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(branch, false); }
    }

    [TestMethod]
    public void AgentContractBuildsQueueWithoutStartingWorkOrGrantingDeletion()
    {
        using var fixture = new VideoPreparationTests.TestFiles();
        using var data = JsonDocument.Parse("""{"title":"Conversion","options":{"codec":"Av1","container":"Mp4","executor":"Cpu","reduction":38,"shortEdge":720,"pictureMayChange":true,"archive":true}}""");
        var schema = JsonSerializer.Serialize(AgentProjects.Describe(ScenarioNavigationCatalog.VideoConverter));
        StringAssert.Contains(schema, "\"shortEdge\"");
        StringAssert.Contains(schema, "\"executor\"");
        Assert.DoesNotContain("deleteSources", schema);
        var created = JsonSerializer.SerializeToElement(AgentProjects.Create(ScenarioNavigationCatalog.VideoConverter, data.RootElement, fixture.Root));
        var path = created.GetProperty("path").GetString()!;
        var draft = JsonSerializer.Deserialize<ConversionDraft>(File.ReadAllText(path), BackgroundOperationStore.Options)!;
        Assert.AreEqual(720, draft.Options.ShortEdge);
        Assert.AreEqual(38, draft.Options.Reduction);
        Assert.AreEqual(ConversionExecutor.Cpu, draft.Options.Executor);
        Assert.IsTrue(draft.Options.Archive);
        Assert.IsNull(draft.JobId);
        var siblings = ScenarioNavigationCatalog.Nodes.Where(n => n.ParentId == "utilities_video").Select(n => n.Id).ToList();
        Assert.AreEqual(siblings.IndexOf(ScenarioNavigationCatalog.VideoUpscale) + 1, siblings.IndexOf(ScenarioNavigationCatalog.VideoConverter));
    }

    [TestMethod]
    public async Task QueueJournalSurvivesABriefReadLock()
    {
        using var fixture = new VideoPreparationTests.TestFiles();
        using var storage = UseWorkspace(fixture);
        var job = new ConversionJob(ConversionStore.NewId(), new() { OutputDirectory = fixture.Root }, []);
        ConversionStore.SaveJob(job);
        var path = ConversionStore.JobPath(job);
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var save = Task.Run(() =>
        {
            writeStarted.SetResult();
            ConversionStore.SaveJob(job with { ArchiveError = "saved" });
        });

        await writeStarted.Task;
        await Task.Delay(55);
        reader.Dispose();
        await save;
        Assert.AreEqual("saved", ConversionStore.ReadJob(job.Id)?.ArchiveError);
    }

    [TestMethod]
    public async Task VerifiedCpuBatchRestoresResultAfterInterruptedCommitAndRetainsAudio()
    {
        using var fixture = new VideoPreparationTests.TestFiles();
        using var storage = UseWorkspace(fixture);
        using var audit = ComponentAudit.UseTestStore(fixture.File("audit"), enabled: true);
        var confirm = ComponentLicenseGate.ConfirmAsync;
        ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        var token = stop.Token;
        try
        {
            var source = fixture.File("Клип с аудио.mkv");
            var next = fixture.File("Второй короткий клип.mkv");
            await WriteClip(source, "384x216", 2.75, true, token);
            await WriteClip(next, "216x384", 1.375, false, token);
            var hash = await UpscaleMediaTools.Hash(source, token);
            var outputFolder = fixture.File("Готовое");
            var options = new ConversionOptions { Executor = ConversionExecutor.Cpu, PictureMayChange = true, Mode = ConversionMode.Size,
                LimitMb = 1, OutputDirectory = outputFolder };
            var first = new ConversionItem(ConversionStore.NewId(), source);
            var second = new ConversionItem(ConversionStore.NewId(), next);
            var bad = new ConversionItem(ConversionStore.NewId(), fixture.File("Недоступный.mkv"));
            var job = new ConversionJob(ConversionStore.NewId(), options, [first, second, bad]);
            ConversionStore.SaveJob(job);
            job = await ConversionBatch.RunAsync(job, new(job.Id, [first.Id]), new Progress<ConversionProgress>(), token);
            first = job.Items[0];
            Assert.AreEqual(ConversionStatus.Ready, first.Status, first.Error);
            var prepared = first.Output!;
            Assert.IsTrue(prepared.Bytes <= 1_000_000);
            var media = await ConversionMedia.ProbeAsync(prepared.Path, token);
            Assert.AreEqual(("av1", 384, 216), (media.Video.Codec, media.Width, media.Height));
            Assert.HasCount(2, media.Tracks.Where(t => t.Type == "audio").ToArray());
            Assert.AreEqual("mini-test", media.Provenance!.Models.Single().Id);
            Assert.AreEqual(hash, await UpscaleMediaTools.Hash(source, token));
            Assert.IsTrue(await ConversionRecycle.CheckAsync(job, first, token));
            Assert.IsNull(job.Archive);

            // A crash after the atomic move can leave the journal a step behind the filesystem.
            job.Items[0] = first with { Status = ConversionStatus.Running, Output = null, Candidate = prepared };
            ConversionStore.SaveJob(job);
            File.Move(source, fixture.File("Исходник перемещён.mkv"));
            job = await ConversionBatch.RunAsync(job, new(job.Id, [first.Id, second.Id, bad.Id]), new Progress<ConversionProgress>(), token);
            Assert.AreEqual(prepared, job.Items[0].Output);
            Assert.AreEqual(ConversionStatus.Ready, job.Items[1].Status, job.Items[1].Error);
            Assert.AreEqual(ConversionStatus.Failed, job.Items[2].Status);
            Assert.IsFalse(await ConversionRecycle.CheckAsync(job, job.Items[0], token));
            var vertical = await ConversionMedia.ProbeAsync(job.Items[1].Output!.Path, token);
            Assert.AreEqual((216, 384), (vertical.Width, vertical.Height));
            Assert.IsTrue(vertical.Bytes <= 1_000_000);
            Assert.HasCount(2, Directory.GetFiles(outputFolder, "*.mp4"));
            Assert.HasCount(0, Directory.GetFiles(outputFolder, "*.zip"));

            var previousOutputHash = await UpscaleMediaTools.Hash(prepared.Path, token);
            job = job with { Options = options with { Archive = true } };
            ConversionStore.SaveJob(job);
            job = await ConversionBatch.RunAsync(job, new(job.Id, []), new Progress<ConversionProgress>(), token);
            Assert.IsNotNull(job.Archive, job.ArchiveError);
            Assert.AreEqual(previousOutputHash, await UpscaleMediaTools.Hash(prepared.Path, token));
        }
        finally { ComponentLicenseGate.ConfirmAsync = confirm; }
    }

    [TestMethod]
    public async Task ZipModeDeliversOnlyTheArchiveAndCanResumeFromACommittedEntry()
    {
        using var fixture = new VideoPreparationTests.TestFiles();
        using var storage = UseWorkspace(fixture);
        using var audit = ComponentAudit.UseTestStore(fixture.File("audit"), enabled: true);
        var confirm = ComponentLicenseGate.ConfirmAsync;
        ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var token = stop.Token;
        try
        {
            var source = fixture.File("one/Клип с аудио.mkv");
            var secondSource = fixture.File("two/Клип с аудио.mkv");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            Directory.CreateDirectory(Path.GetDirectoryName(secondSource)!);
            await WriteClip(source, "384x216", 1.5, true, token);
            await WriteClip(secondSource, "216x384", 1.25, false, token);
            var sourceHash = await UpscaleMediaTools.Hash(source, token);
            var outputFolder = fixture.File("Готовое");
            var first = new ConversionItem(ConversionStore.NewId(), source);
            var second = new ConversionItem(ConversionStore.NewId(), secondSource);
            var job = new ConversionJob(ConversionStore.NewId(), new()
            {
                Executor = ConversionExecutor.Cpu, PictureMayChange = true, Mode = ConversionMode.Size,
                LimitMb = 1, Archive = true, OutputDirectory = outputFolder
            }, [first, second]);
            ConversionStore.SaveJob(job);

            // A file at the destination prevents the ZIP commit while the encoded video remains recoverable.
            File.WriteAllText(outputFolder, "occupied");
            job = await ConversionBatch.RunAsync(job, new(job.Id, [first.Id]), new Progress<ConversionProgress>(), token);
            Assert.AreEqual(ConversionStatus.Ready, job.Items[0].Status, job.Items[0].Error);
            Assert.IsNotNull(job.ArchiveError);
            Assert.IsNull(job.Archive);
            var staged = job.Items[0].Output!;
            Assert.IsTrue(staged.Path.StartsWith(ConversionStore.Scratch(job) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(File.Exists(staged.Path));
            Assert.IsFalse(await ConversionRecycle.CheckAsync(job, job.Items[0], token));

            File.Delete(outputFolder);
            job = ConversionStore.ReadJob(job.Id)!;
            job = await ConversionBatch.RunAsync(job, new(job.Id, []), new Progress<ConversionProgress>(), token);
            Assert.IsNotNull(job.Archive, job.ArchiveError);
            Assert.IsFalse(File.Exists(staged.Path));
            CollectionAssert.AreEqual(new[] { job.Archive.Path }, Directory.GetFileSystemEntries(outputFolder));
            Assert.IsTrue(await ConversionRecycle.CheckAsync(job, job.Items[0], token));
            Assert.AreEqual(sourceHash, await UpscaleMediaTools.Hash(source, token));

            var archive = job.Archive;
            var extracted = fixture.File("проверка.mp4");
            using (var zip = ZipFile.OpenRead(archive.Path))
            {
                Assert.HasCount(1, zip.Entries);
                zip.Entries[0].ExtractToFile(extracted);
            }
            var media = await ConversionMedia.ProbeAsync(extracted, token);
            Assert.AreEqual(("av1", 384, 216), (media.Video.Codec, media.Width, media.Height));
            Assert.HasCount(2, media.Tracks.Where(track => track.Type == "audio").ToArray());

            // Simulate the crash after publishing the ZIP, with the journal still describing a candidate.
            job = job with { Archive = null, ArchiveCandidate = archive };
            ConversionStore.SaveJob(job);
            File.Move(source, fixture.File("Исходник перемещён.mkv"));
            job = await ConversionBatch.RunAsync(job, new(job.Id, [first.Id, second.Id]), new Progress<ConversionProgress>(), token);
            Assert.IsNotNull(job.Archive, job.ArchiveError);
            Assert.AreEqual(staged, job.Items[0].Output);
            Assert.AreEqual(ConversionStatus.Ready, job.Items[1].Status, job.Items[1].Error);
            Assert.IsFalse(File.Exists(job.Items[1].Output!.Path));
            Assert.AreEqual(archive.Path, job.Archive.Path);
            CollectionAssert.AreEqual(new[] { archive.Path }, Directory.GetFileSystemEntries(outputFolder));
            using (var zip = ZipFile.OpenRead(archive.Path))
            {
                Assert.HasCount(2, zip.Entries);
                Assert.AreNotEqual(zip.Entries[0].Name, zip.Entries[1].Name);
            }
            Assert.IsFalse(await ConversionRecycle.CheckAsync(job, job.Items[0], token));
            Assert.IsTrue(await ConversionRecycle.CheckAsync(job, job.Items[1], token));

            var delivered = job.Archive;
            job = job with { Items = [job.Items[1]] };
            ConversionStore.SaveJob(job);
            job = await ConversionBatch.RunAsync(job, new(job.Id, []), new Progress<ConversionProgress>(), token);
            Assert.IsNotNull(job.Archive, job.ArchiveError);
            Assert.AreNotEqual(delivered.Path, job.Archive.Path);
            Assert.IsTrue(await ConversionStore.VerifyFile(delivered.Path, delivered, token));
        }
        finally { ComponentLicenseGate.ConfirmAsync = confirm; }
    }

    [TestMethod]
    public async Task TurningOffZipDeliversTheStagedVideoAndDiscardsTheUnfinishedArchive()
    {
        using var fixture = new VideoPreparationTests.TestFiles();
        using var storage = UseWorkspace(fixture);
        using var audit = ComponentAudit.UseTestStore(fixture.File("audit"), enabled: true);
        var confirm = ComponentLicenseGate.ConfirmAsync;
        ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        var token = stop.Token;
        try
        {
            var input = fixture.File("Видео.mkv");
            await WriteClip(input, "384x216", 1.25, false, token);
            var destination = fixture.File("Готовое");
            var item = new ConversionItem(ConversionStore.NewId(), input);
            var job = new ConversionJob(ConversionStore.NewId(), new()
            {
                Executor = ConversionExecutor.Cpu, PictureMayChange = true, Mode = ConversionMode.Size,
                LimitMb = 1, Archive = true, OutputDirectory = destination
            }, [item]);
            ConversionStore.SaveJob(job);
            File.WriteAllText(destination, "occupied");
            job = await ConversionBatch.RunAsync(job, new(job.Id, [item.Id]), new Progress<ConversionProgress>(), token);
            Assert.IsNotNull(job.ArchiveCandidate, job.ArchiveError);
            var unfinished = ConversionStore.ScratchFile(ConversionStore.Scratch(job), job.Id, ".zip", "archive");
            Assert.IsTrue(File.Exists(unfinished));
            var staged = job.Items[0].Output!;

            File.Delete(destination);
            File.Move(input, fixture.File("Исходник перемещён.mkv"));
            job = job with { Options = job.Options with { Archive = false } };
            ConversionStore.SaveJob(job);
            job = await ConversionBatch.RunAsync(job, new(job.Id, [item.Id]), new Progress<ConversionProgress>(), token);
            Assert.AreEqual(ConversionStatus.Ready, job.Items[0].Status, job.Items[0].Error);
            var saved = job.Items[0].Output!;
            Assert.AreEqual(staged.Sha256, saved.Sha256);
            CollectionAssert.AreEqual(new[] { saved.Path }, Directory.GetFileSystemEntries(destination));
            Assert.IsNull(job.ArchiveCandidate);
            Assert.IsNull(job.ArchiveError);
            Assert.IsFalse(File.Exists(unfinished));
            Assert.IsFalse(File.Exists(staged.Path));
        }
        finally { ComponentLicenseGate.ConfirmAsync = confirm; }
    }

    private static IDisposable UseWorkspace(VideoPreparationTests.TestFiles fixture)
    {
        var root = fixture.File("рабочая");
        Directory.CreateDirectory(root);
        return ManagedWorkStorage.Use(new(fixture.File("profile"), () => new() { Locations = [new() { Path = root }] }));
    }

    [DataRow("Hevc", "Mp4", "hevc", "libkvazaar")]
    [DataRow("Avc", "Mp4", "h264", "libopenh264")]
    [DataRow("Vp9", "WebM", "vp9", "libvpx-vp9")]
    [DataRow("Ffv1", "Mkv", "ffv1", "ffv1")]
    [TestMethod]
    public async Task CpuProfilesDecodeAndFfv1RequiresApprovalAboveLimit(string codec, string container, string expected, string encoder)
    {
        using var fixture = new VideoPreparationTests.TestFiles();
        using var storage = UseWorkspace(fixture);
        using var audit = ComponentAudit.UseTestStore(fixture.File("audit"), enabled: false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = timeout.Token;
        var source = fixture.File("Исходный кадр.mp4");
        await UpscaleFfmpeg.RunAsync(false, ["-v", "error", "-f", "lavfi", "-i", "testsrc2=size=384x216:rate=24:duration=1.25",
            "-c:v", "libopenh264", "-b:v", "1200k", source], token);
        var lossless = codec == "Ffv1";
        var options = new ConversionOptions
        {
            Codec = Enum.Parse<ConversionCodec>(codec), Container = Enum.Parse<ConversionContainer>(container),
            Executor = ConversionExecutor.Cpu, Mode = lossless ? ConversionMode.Percent : ConversionMode.Quality,
            Reduction = 95, OutputDirectory = fixture.File("Результаты"), PictureMayChange = !lossless
        };
        var item = new ConversionItem(ConversionStore.NewId(), source);
        var job = new ConversionJob(ConversionStore.NewId(), options, [item]);
        ConversionStore.SaveJob(job);
        job = await ConversionBatch.RunAsync(job, new(job.Id, [item.Id]), new Progress<ConversionProgress>(), token);
        if (lossless)
        {
            Assert.AreEqual(ConversionStatus.OverLimit, job.Items[0].Status, job.Items[0].Error);
            Assert.IsFalse(File.Exists(job.Items[0].Candidate!.Path));
            job = await ConversionBatch.RunAsync(job, new(job.Id, [item.Id], item.Id), new Progress<ConversionProgress>(), token);
        }
        Assert.AreEqual(ConversionStatus.Ready, job.Items[0].Status, job.Items[0].Error);
        var output = job.Items[0].Output!;
        StringAssert.Contains(output.Details, encoder);
        var media = await ConversionMedia.ProbeAsync(output.Path, token);
        Assert.AreEqual((expected, 384, 216), (media.Video.Codec, media.Width, media.Height));
        if (lossless)
            CollectionAssert.AreEqual(await FrameHashes(source, token), await FrameHashes(output.Path, token));
    }

    private static async Task<string[]> FrameHashes(string path, CancellationToken token)
    {
        var data = await UpscaleFfmpeg.RunAsync(false, ["-v", "error", "-i", path,
            "-map", "0:v:0", "-c:v", "rawvideo", "-f", "framehash", "pipe:1"], token);
        return data.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.StartsWith('#')).Select(line => line.Split(',').Last().Trim()).ToArray();
    }

    [TestMethod]
    public async Task Av1Mp4PreservesColorSitingSlowerFrameRateAndCoverArt()
    {
        using var fixture = new VideoPreparationTests.TestFiles();
        using var storage = UseWorkspace(fixture);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = timeout.Token;
        var cover = fixture.File("Обложка.png");
        var source = fixture.File("Исходник 23,976.mp4");
        await UpscaleFfmpeg.RunAsync(false, ["-v", "error", "-f", "lavfi", "-i", "color=blue:size=64x64:rate=1",
            "-frames:v", "1", "-c:v", "png", cover], token);
        await UpscaleFfmpeg.RunAsync(false, ["-v", "error", "-f", "lavfi", "-i", "testsrc2=size=384x216:rate=24000/1001:duration=1.5",
            "-i", cover, "-map", "0:v:0", "-map", "1:v:0", "-c:v:0", "libopenh264", "-b:v:0", "1200k", "-c:v:1", "copy",
            "-disposition:v:1", "attached_pic", "-color_range:v:0", "tv", "-colorspace:v:0", "bt709",
            "-color_primaries:v:0", "bt709", "-color_trc:v:0", "bt709", "-metadata", "comment=Исходная заметка", source], token);
        var before = await ConversionMedia.ProbeAsync(source, token);
        Assert.AreEqual("left", before.Color["chroma_location"]);
        Assert.AreEqual("png", before.Tracks.Single(track => track.IsArtwork).Codec);
        var options = new ConversionOptions { Executor = ConversionExecutor.Cpu, Mode = ConversionMode.Quality,
            PictureMayChange = true, MotionMayChange = true, FramesPerSecond = 24, OutputDirectory = fixture.File("Готовое") };
        var item = new ConversionItem(ConversionStore.NewId(), source);
        var job = new ConversionJob(ConversionStore.NewId(), options, [item]);
        ConversionStore.SaveJob(job);
        job = await ConversionBatch.RunAsync(job, new(job.Id, [item.Id]), new Progress<ConversionProgress>(), token);
        Assert.AreEqual(ConversionStatus.Ready, job.Items[0].Status, job.Items[0].Error);
        var result = await ConversionMedia.ProbeAsync(job.Items[0].Output!.Path, token);
        Assert.AreEqual(".mp4", job.Items[0].Output!.Extension);
        Assert.AreEqual("av1", result.Video.Codec);
        Assert.AreEqual(before.Fps, result.Fps, .002);
        Assert.AreEqual("png", result.Tracks.Single(track => track.IsArtwork).Codec);
        Assert.AreEqual("Исходная заметка", result.Comment);
        Assert.IsNotNull(result.Provenance);
        Assert.IsNotNull(result.ConversionTag);
        foreach (var (property, value) in before.Color) Assert.AreEqual(value, result.Color.GetValueOrDefault(property), property);
    }

    [TestMethod]
    public async Task IncompatibleContainerFallsBackWhileKeepingCodecAndOriginalAudio()
    {
        using var fixture = new VideoPreparationTests.TestFiles();
        using var storage = UseWorkspace(fixture);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = timeout.Token;
        var source = fixture.File("Ролик со звуком PCM.mkv");
        await UpscaleFfmpeg.RunAsync(false, ["-v", "error", "-f", "lavfi", "-i", "testsrc2=size=384x216:rate=24:duration=1.5",
            "-f", "lavfi", "-i", "sine=frequency=400:sample_rate=48000:duration=1.5", "-c:v", "ffv1", "-c:a", "pcm_s16le", source], token);
        var options = new ConversionOptions { Executor = ConversionExecutor.Cpu, Mode = ConversionMode.Quality,
            PictureMayChange = true, OutputDirectory = fixture.File("Готовое") };
        var item = new ConversionItem(ConversionStore.NewId(), source);
        var job = new ConversionJob(ConversionStore.NewId(), options, [item]);
        ConversionStore.SaveJob(job);
        job = await ConversionBatch.RunAsync(job, new(job.Id, [item.Id]), new Progress<ConversionProgress>(), token);
        Assert.AreEqual(ConversionStatus.Ready, job.Items[0].Status, job.Items[0].Error);
        var output = job.Items[0].Output!;
        Assert.AreEqual(".mkv", output.Extension);
        StringAssert.Contains(output.Details, "AV1 / MP4 → AV1 / MKV");
        var result = await ConversionMedia.ProbeAsync(output.Path, token);
        Assert.AreEqual("av1", result.Video.Codec);
        Assert.AreEqual("pcm_s16le", result.Tracks.Single(track => track.Type == "audio").Codec);
        Assert.AreEqual(ConversionContainer.Mp4, job.Options.Container);
    }

    private static Task WriteClip(string path, string dimensions, double duration, bool sound, CancellationToken token)
    {
        var length = ConversionPlan.Number(duration);
        var facts = new OutputProvenance { Scenario = "test/original", Models = [new("mini-test", "Модель теста", "1", "video")] };
        string[] tracks = sound ? [
            "-f", "lavfi", "-i", $"sine=frequency=440:sample_rate=48000:duration={length}",
            "-f", "lavfi", "-i", $"sine=frequency=660:sample_rate=48000:duration={length}",
            "-map", "0:v", "-map", "1:a", "-map", "2:a", "-c:a", "aac",
            "-metadata:s:a:0", "language=eng", "-metadata:s:a:1", "language=rus"
        ] : [];
        return UpscaleFfmpeg.RunAsync(false, ["-v", "error", "-f", "lavfi", "-i", $"testsrc2=size={dimensions}:rate=24:duration={length}", .. tracks,
            "-c:v", "ffv1", "-metadata", "LOPATA_VIDEO=" + facts.Json(), path], token);
    }
}
