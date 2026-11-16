using System.Globalization;
using System.IO;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Controls;
using AIHub.Services;
using AIHub.Services.Shorts;
using AIHub.Services.SongClip;
using AIHub.Services.VideoPreparation;
using AIHub.Services.VideoEditor;
using AIHub.Services.VideoUpscale;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class ShortsTests
{
    [TestMethod]
    public void LyricsAreClippedAndRebasedToTheChosenExcerpt()
    {
        var input = "[00:06.00]opening\n[00:15.00]phrase at cut\n[00:21.50]inside\n[00:32.00]outside";
        var (lyrics, timings) = ShortsJob.SliceTimings(input, 17, 15);
        Assert.AreEqual("phrase at cut\ninside", lyrics);
        Assert.AreEqual("[00:00.00]phrase at cut\n[00:04.50]inside", timings);
        ShortsJob.ValidateRange(17, 15, 32);
        Assert.ThrowsExactly<InvalidDataException>(() => ShortsJob.ValidateRange(17.02, 15, 32));
    }

    [TestMethod, Timeout(60000)]
    public async Task AudioCutUsesTheSelectedPartEvenNearTheEnd()
    {
        using var files = new VideoPreparationTests.TestFiles();
        using var audit = ComponentAudit.UseTestStore(files.File("audit"), enabled: false);
        var song = files.File("original.wav");
        var excerpt = files.File("excerpt.wav");
        var raw = files.File("original.pcm");
        var actual = files.File("actual.pcm");
        const int sampleRate = 48000;
        using (var writer = new BinaryWriter(File.Create(raw)))
            for (var index = 0; index < 20 * sampleRate; index++)
                writer.Write((short)(index < 5 * sampleRate ? 1000 : index % 8000 - 4000));

        await VideoRuntime.PrepareAsync(default);
        await VideoRuntime.RunAsync(false, ["-v", "error", "-y", "-f", "s16le", "-ar", "48000", "-ac", "1",
            "-i", raw, "-c:a", "pcm_s16le", song], default);
        await ShortsJob.CutSongAsync(song, excerpt, 5, 15, default);
        await VideoRuntime.RunAsync(false, ["-v", "error", "-y", "-i", excerpt, "-f", "s16le", actual], default);
        var fullPcm = File.ReadAllBytes(raw);
        var excerptPcm = File.ReadAllBytes(actual);
        Assert.AreEqual(15 * sampleRate * 2, excerptPcm.Length);
        Assert.IsTrue(excerptPcm.AsSpan().SequenceEqual(fullPcm.AsSpan(5 * sampleRate * 2)));
    }

    [TestMethod]
    public void EachImageGetsItsOwnCharacterAndTextOnlyWorks()
    {
        const string prompt = "A cook meets a pilot. They say hello.";
        Assert.AreEqual(prompt, ShortsJob.Describe(prompt, 0));
        var described = ShortsJob.Describe(prompt, 2);
        StringAssert.StartsWith(described, prompt);
        StringAssert.Contains(described, "Reference image 1 depicts character 1");
        StringAssert.Contains(described, "Reference image 2 depicts character 2");
        ShortsJob.Validate(new(Prompt: prompt));
        Assert.AreEqual("crop=756:1344:(iw-ow)/2:(ih-oh)/2", ShortsRunner.Crop(768, 1344));
    }

    [TestMethod]
    public Task CatalogOpensTheCompactWorkspaceAndBackReturnsToVideo() => ScenarioNavigationTests.Sta(() =>
    {
        var page = new ScenarioNavigationControl();
        page.OpenScenario(ScenarioNavigationCatalog.Shorts);
        Assert.IsTrue(page.IsShorts);
        Assert.AreEqual(ScenarioNavigationCatalog.Shorts, page.RecoveryScenarioId);
        var controls = ScenarioNavigationTests.LogicalDescendants(page.Shorts!).ToArray();
        Assert.IsTrue(controls.Any(control => AutomationProperties.GetAutomationId(control) == "Shorts.Create"));
        Assert.IsTrue(controls.OfType<CheckBox>().Any(control => AutomationProperties.GetAutomationId(control) == "Shorts.FadeEnding"));
        var duration = controls.OfType<ComboBox>().Single(control => AutomationProperties.GetAutomationId(control) == "Shorts.Duration");
        CollectionAssert.AreEqual(new[] { 15, 30, 60, 90, 120, 150, 179 }, duration.Items.OfType<ComboBoxItem>().Select(item => (int)item.Tag).ToArray());
        Assert.AreEqual("2:59", duration.Items.OfType<ComboBoxItem>().Last().Content);
        Assert.IsTrue(page.GoBack());
        Assert.IsFalse(page.IsShorts);
        Assert.AreEqual(ScenarioNavigationCatalog.Creation, page.SelectedDirectionId);
    });

    [TestMethod]
    public void ProgressUsesRealFragmentAndStepCountersForBothModes()
    {
        var progress = new ShortsProgress()
            .Update("""{"kind":"planner","current":1,"total":1}""")
            .Update("""{"kind":"chunk-start","index":2,"total":4}""")
            .Update("""{"kind":"progress","index":2,"total":4,"step":3,"steps":8}""");
        Assert.AreEqual(ShortsStage.Generate, progress.Stage);
        Assert.AreEqual((2, 4, 3, 8), (progress.Fragment, progress.Total, progress.Step, progress.Steps));
        Assert.IsFalse(progress.EstimatedTotal);
        Assert.AreSame(progress, progress.Update("""{"kind":"hardware","device":"cuda:0"}"""));
        progress = progress.Update("Shorts.Upscaling");
        Assert.AreEqual(ShortsStage.Upscale, progress.Stage);
        Assert.AreEqual(0, progress.Steps);
        Assert.AreEqual(ShortsStage.Save, progress.Update("Shorts.Exporting").Stage);

        var description = new ShortsProgress()
            .Update("""[H3 event] {"kind":"pass","index":2,"total":5}""")
            .Update("""[H3 event] {"kind":"stage","text":"sampling"}""")
            .Update("""[H3 event] {"kind":"progress","value":7,"total":8}""");
        Assert.AreEqual((2, 5, 7, 8), (description.Fragment, description.Total, description.Step, description.Steps));
        Assert.IsTrue(description.EstimatedTotal);
        Assert.AreEqual(0, description.Update("""[H3 event] {"kind":"stage","text":"decoding"}""").Steps);
        Assert.AreSame(description, description.Update("[H3 event] x"));
    }

    [TestMethod]
    public void ExcerptSlidesWithinSongAndChosenOutputRequiresAnUnusedMp4()
    {
        Assert.AreEqual(13.17, ShortsControl.ClampExcerptStart(90, 28.175, 15));
        Assert.AreEqual(0, ShortsControl.ClampExcerptStart(-4, 28, 15));
        Assert.AreEqual(0, ShortsControl.ClampExcerptStart(9, 12, 15));
        Assert.AreEqual(24.76, ShortsControl.ClampExcerptStart(24.761, 90, 30));
        using var files = new VideoPreparationTests.TestFiles();
        var target = files.File("Мои шортсы.mp4");
        Assert.AreEqual(target, ShortsJob.ValidateOutputPath(target));
        Assert.ThrowsExactly<InvalidDataException>(() => ShortsJob.ValidateOutputPath(files.File("video.mov")));
        File.WriteAllText(target, "existing file");
        Assert.ThrowsExactly<IOException>(() => ShortsJob.ValidateOutputPath(target));
        Assert.AreEqual("existing file", File.ReadAllText(target));
        var legacy = System.Text.Json.JsonSerializer.Deserialize<ShortsDraft>("{}");
        Assert.IsNull(legacy!.OutputPath);
        Assert.AreEqual(15d, legacy.SongSeconds);
        Assert.IsFalse(legacy.FadeEnding);
    }

    [TestMethod]
    public Task ThemedWorkspaceShowsOutputAndReferencePreviewInTheSongMode() => ScenarioNavigationTests.Sta(() =>
    {
        using var files = new VideoPreparationTests.TestFiles();
        var reference = files.File("portrait.webp");
        using var bitmap = new SkiaSharp.SKBitmap(36, 64);
        bitmap.Erase(SkiaSharp.SKColors.DodgerBlue);
        using (var image = SkiaSharp.SKImage.FromBitmap(bitmap))
        using (var bytes = image.Encode(SkiaSharp.SKEncodedImageFormat.Webp, 90))
        using (var stream = File.Create(reference))
            bytes.SaveTo(stream);

        var localization = new LocalizationService();
        localization.Load("ru");
        var target = files.File("Шортс.mp4");
        var control = new ShortsControl(localization.T,
            new(FromSong: true, References: [new(reference, "Исполнительница")], OutputPath: target));
        var controls = ScenarioNavigationTests.LogicalDescendants(control).OfType<System.Windows.FrameworkElement>().ToArray();
        var button = controls.OfType<Button>().Single(item => AutomationProperties.GetAutomationId(item) == "Shorts.Song");
        var output = controls.OfType<TextBox>().Single(item => AutomationProperties.GetAutomationId(item) == "Shorts.OutputPath");
        var caption = controls.OfType<TextBlock>().Single(item => item.Text == "Шортсы");
        Assert.AreEqual(target, output.Text);
        Assert.IsFalse(controls.OfType<CheckBox>().Single(item => AutomationProperties.GetAutomationId(item) == "Shorts.FadeEnding").IsChecked);
        Assert.IsNotNull(controls.OfType<Image>().SingleOrDefault(item => AutomationProperties.GetAutomationId(item) == "Shorts.Reference.1.Preview"));
        Assert.IsNotNull(controls.OfType<AIHub.Controls.VideoUpscale.UpscaleRange>().SingleOrDefault());
        var preview = (System.Windows.Media.Imaging.BitmapSource)ShortsControl.ReadReferencePreview(reference);
        Assert.AreEqual(36, preview.PixelWidth);
        Assert.AreEqual(64, preview.PixelHeight);
        Assert.IsTrue(preview.IsFrozen);

        foreach (var (foreground, background) in new[] { (System.Windows.Media.Brushes.White, System.Windows.Media.Brushes.DarkSlateGray),
            (System.Windows.Media.Brushes.Black, System.Windows.Media.Brushes.WhiteSmoke) })
        {
            control.Resources["TextPrimaryBrush"] = foreground;
            control.Resources["SecondaryButtonBackgroundBrush"] = background;
            control.Measure(new System.Windows.Size(760, 640));
            control.Arrange(new System.Windows.Rect(0, 0, 760, 640));
            control.UpdateLayout();
            Assert.AreSame(foreground, caption.Foreground);
            Assert.AreSame(foreground, button.Foreground);
            Assert.AreSame(background, button.Background);
            Assert.AreSame(background, output.Background);
            Assert.IsNotNull(button.Template.FindName("Frame", button));
        }
    });

    [TestMethod, Timeout(180000)]
    public Task VerticalExportPreservesAudioAndWritesExactFramesAndProvenance() => VerifyExportAsync(null);

    [TestMethod, Timeout(300000)]
    public async Task InstalledDlssUpscalesVerticalVideoWhenRequested()
    {
        var root = Environment.GetEnvironmentVariable("LOPATA_SHORTS_DLSS_MODELS");
        if (string.IsNullOrEmpty(root)) { Assert.Inconclusive("Opt-in installed DLSS test."); return; }
        await VerifyExportAsync(root);
    }

    [TestMethod, Timeout(2100000)]
    public async Task InstalledH3ProducesFifteenSecondsFromDescriptionWhenRequested()
    {
        var root = Environment.GetEnvironmentVariable("LOPATA_SHORTS_H3_MODELS");
        var output = Environment.GetEnvironmentVariable("LOPATA_SHORTS_H3_OUTPUT");
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(output)) { Assert.Inconclusive("Opt-in installed H3 test."); return; }
        var cards = ShortsJob.Cards(false, root);
        Assert.IsFalse(SongClipAutomaticPreparation.NeedsDownload(cards), "The installed-only test requires prepared H3 source files.");
        const string prompt = "A static vertical shot of a small old brass clock on a workbench in sunlight. The second hand moves and we hear the clock ticking.";
        var resumeId = Environment.GetEnvironmentVariable("LOPATA_SHORTS_H3_RESUME");
        var staged = resumeId is null ? await ShortsJob.CreateAsync(new(Prompt: prompt), root, 4, default) : ShortsJob.Load(resumeId);
        var job = staged with { Output = output };
        job.Save();
        var h3 = job.Description!;
        var report = output + ".log";
        File.AppendAllText(report, $"Job {job.Id}; H3 run {h3.Run.Id}; saved parts {h3.Run.Takes.Count}{Environment.NewLine}");
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(32));
        void Log(string line)
        {
            lock (h3) File.AppendAllText(report, $"{DateTimeOffset.UtcNow:o} {line}{Environment.NewLine}");
        }
        var result = await ShortsRunner.StartAsync(job, "Shorts test", Log, stop.Token);
        Assert.AreEqual(output, result);
        Assert.IsTrue(File.Exists(output));
        var completed = ShortsJob.Load(job.Id);
        Assert.IsTrue(completed.Description!.Run.Complete);
        Assert.IsGreaterThan(1, completed.Description.Run.Takes.Count);
    }

    [TestMethod]
    public Task TimelineRendersSongPeaksInsideAndOutsideTheSelection() => ScenarioNavigationTests.Sta(() =>
    {
        var peaks = new float[1000];
        Array.Fill(peaks, .9f, 250, 250);
        var timeline = new AIHub.Controls.VideoUpscale.UpscaleRange(compact: true)
        {
            Waveform = new VideoWaveform(192, peaks)
        };
        timeline.Resources["PanelBrush"] = System.Windows.Media.Brushes.Black;
        timeline.Resources["StepBadgeBrush"] = System.Windows.Media.Brushes.DarkBlue;
        timeline.Resources["TextSecondaryBrush"] = System.Windows.Media.Brushes.White;
        timeline.Resources["AccentBrush"] = System.Windows.Media.Brushes.Blue;
        timeline.Set(4, 1.25, 1.5, 1, 15);
        timeline.Measure(new System.Windows.Size(400, 76));
        timeline.Arrange(new System.Windows.Rect(0, 0, 400, 76));
        timeline.UpdateLayout();

        var image = new System.Windows.Media.Imaging.RenderTargetBitmap(400, 76, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        image.Render(timeline);
        var pixels = new byte[400 * 76 * 4];
        image.CopyPixels(pixels, 400 * 4, 0);
        byte Red(int x) => pixels[(35 * 400 + x) * 4 + 2];
        Assert.AreEqual(0, (int)Red(51));
        Assert.IsGreaterThan(100, (int)Red(111));
        Assert.IsGreaterThan(100, (int)Red(151));
    });

    [TestMethod, Timeout(60000)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ShortenedSongExcerptUsesWaveformAndFadesBothStreamsInTheLastSecond(bool useLosslessUpscale)
    {
        using var test = new VideoPreparationTests.TestFiles();
        using var audit = ComponentAudit.UseTestStore(test.File("audit"), enabled: false);
        const int sampleRate = 48000;
        const double start = .5, seconds = 2.75;
        var raw = test.File("source.pcm");
        using (var writer = new BinaryWriter(File.Create(raw)))
            for (var sample = 0; sample < 4 * sampleRate; sample++)
                writer.Write((short)(sample < sampleRate ? 0 : 13000 * Math.Sin(2 * Math.PI * 440 * sample / sampleRate)));
        var song = test.File("source.wav");
        await VideoRuntime.PrepareAsync(default);
        await VideoRuntime.RunAsync(false, ["-v", "error", "-y", "-f", "s16le", "-ar", "48000", "-ac", "1",
            "-i", raw, "-c:a", "pcm_s16le", song], default);
        var wave = await VideoWaveform.ReadAsync(song, 0, 4, default);
        Assert.AreEqual(0d, wave.Peak(.5, .9));
        Assert.IsGreaterThan(.25, wave.Peak(1.1, 3));

        var preview = test.File("preview.wav");
        await ShortsJob.CutSongAsync(song, preview, start, seconds, default, fadeEnding: true);
        var excerpt = test.File("excerpt.wav");
        await ShortsJob.CutSongAsync(song, excerpt, start, seconds, default);
        var input = test.File("input.mp4");
        var facts = new OutputProvenance
        {
            Scenario = "song-clip", Models = [new("test-model", "Fixture", "1", "video")]
        };
        await UpscaleFfmpeg.PrepareAsync(default);
        await UpscaleFfmpeg.RunAsync(false, ["-v", "error", "-y", "-f", "lavfi", "-i", "color=c=white:s=192x320:r=24",
            "-i", excerpt, "-shortest", "-c:v", "libopenh264", "-c:a", "aac",
            "-metadata", "LOPATA_VIDEO=" + facts.Json(), "-movflags", "+use_metadata_tags", input], default);
        var job = new ShortsJob(Guid.NewGuid().ToString("N"), true, 15, test.File("no-models"), 2,
            test.File("export.mp4"), DateTimeOffset.UtcNow, null, null, ExcerptFrames: 66, FadeEnding: true);
        if (useLosslessUpscale)
        {
            var intermediate = test.File("upscale.mkv");
            await UpscaleFfmpeg.RunAsync(false, ["-v", "error", "-y", "-f", "lavfi", "-i", "color=c=white:s=1080x1920:r=24:d=2.75",
                "-an", "-pix_fmt", "gbrp16le", "-c:v", "ffv1", intermediate], default);
            await ShortsRunner.ExportAsync(job, input, "", facts, (intermediate, new("test-dlss", "DLSS fixture", "1", "DLSS Super Resolution")), default);
            using var probe = await VideoRuntime.ProbeAsync(job.Output, default);
            Assert.Contains("test-dlss", probe.RootElement.GetProperty("format").GetProperty("tags").GetProperty("LOPATA_VIDEO").GetString()!);
        }
        else
        {
            await ShortsRunner.PublishAsync(job, input, _ => { }, default);
        }

        var frames = test.File("frames.raw");
        await VideoRuntime.RunAsync(false, ["-v", "error", "-y", "-i", job.Output, "-an",
            "-vf", "scale=2:2,format=rgb24", "-threads:v", "1", "-c:v", "rawvideo", "-f", "rawvideo", frames], default);
        var pixels = File.ReadAllBytes(frames);
        Assert.AreEqual(66 * 12, pixels.Length);
        Assert.IsGreaterThan(245, (int)pixels[42 * 12]); // 1.75 s, the beginning of the fade
        Assert.IsTrue(pixels[54 * 12] is > 100 and < 150); // middle of the fade
        Assert.IsLessThanOrEqualTo(3, (int)pixels[^1]); // black on the last displayed frame

        foreach (var file in new[] { preview, job.Output })
        {
            var samples = test.File("decoded.pcm");
            await VideoRuntime.RunAsync(false, ["-v", "error", "-y", "-i", file, "-vn", "-ac", "1", "-ar", "48000",
                "-c:a", "pcm_s16le", "-f", "s16le", samples], default);
            var audio = File.ReadAllBytes(samples);
            int Peak(double at) => Enumerable.Range((int)(at * sampleRate), sampleRate / 50)
                .Max(sample => Math.Abs((int)BitConverter.ToInt16(audio, sample * 2)));
            Assert.IsLessThan(150, Peak(.2));
            Assert.IsGreaterThan(11000, Peak(1.70));
            Assert.IsTrue(Peak(2.25) is > 5000 and < 8000);
            Assert.IsLessThan(1200, Peak(2.69));
        }
    }

    [TestMethod, Timeout(120000)]
    public Task MaximumDurationClipsHalfSecondOverrunFromBothTracks()
    {
        ShortsJob.Validate(new(Seconds: 179, Prompt: "A short vertical video."));
        Assert.ThrowsExactly<InvalidDataException>(() => ShortsJob.Validate(new(Seconds: 180, Prompt: "Too long.")));
        return VerifyExportAsync(null, seconds: 179, extraSeconds: .5);
    }

    private static async Task VerifyExportAsync(string? models, int seconds = 15, double extraSeconds = 0)
    {
        using var test = new VideoPreparationTests.TestFiles();
        using var audit = ComponentAudit.UseTestStore(test.File("audit"), enabled: false);
        var original = new OutputProvenance
        {
            Scenario = "video-generation/minimax-h3", ModelHistory = "synthetic-test",
            Models = [new("test-model", "Fixture", "1", "video and audio")]
        };
        var input = test.File("input.mp4");
        var job = new ShortsJob(Guid.NewGuid().ToString("N"), false, seconds, models ?? test.File("no-models"), 2,
            test.File("export.mp4"), DateTimeOffset.UtcNow, null, null);

        await UpscaleFfmpeg.PrepareAsync(default);
        var inputSize = extraSeconds > 0 ? "180x320" : "768x1344";
        var inputSeconds = (seconds + extraSeconds).ToString(CultureInfo.InvariantCulture);
        await UpscaleFfmpeg.RunAsync(false, ["-v", "error", "-y", "-f", "lavfi", "-i", $"color=c=royalblue:s={inputSize}:r=24",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", inputSeconds, "-c:v", "libopenh264",
            "-c:a", "aac", "-metadata", "LOPATA_VIDEO=" + original.Json(), "-movflags", "+use_metadata_tags", input], default);
        var stages = new List<string>();
        await ShortsRunner.PublishAsync(job, input, stages.Add, default);

        using var probe = await VideoRuntime.ProbeAsync(job.Output, default);
        var format = probe.RootElement.GetProperty("format");
        StringAssert.Contains(format.GetProperty("tags").GetProperty("LOPATA_VIDEO").GetString()!, "shorts/from-description");
        StringAssert.Contains(format.GetProperty("tags").GetProperty("LOPATA_VIDEO").GetString()!, "test-model");
        if (models is null) Assert.Contains("Shorts.SoftwareScale", stages);
        else
        {
            Assert.Contains("Shorts.Dlss", stages);
            Assert.DoesNotContain("Shorts.SoftwareScale", stages);
            StringAssert.Contains(format.GetProperty("tags").GetProperty("LOPATA_VIDEO").GetString()!, "DLSS Super Resolution");
        }
        var streams = probe.RootElement.GetProperty("streams");
        Assert.AreEqual((seconds * 24).ToString(CultureInfo.InvariantCulture), streams[0].GetProperty("nb_frames").GetString());
        var expectedSeconds = seconds.ToString("F6", CultureInfo.InvariantCulture);
        Assert.AreEqual(expectedSeconds, format.GetProperty("duration").GetString());
        Assert.AreEqual(expectedSeconds, streams[0].GetProperty("duration").GetString());
        Assert.AreEqual(expectedSeconds, streams[1].GetProperty("duration").GetString());
        Assert.AreEqual(1080, streams[0].GetProperty("width").GetInt32());
        Assert.AreEqual(1920, streams[0].GetProperty("height").GetInt32());
        Assert.AreEqual("aac", streams[1].GetProperty("codec_name").GetString());
        Assert.IsFalse(File.Exists(job.Output + ".partial.mp4"));
    }
}
