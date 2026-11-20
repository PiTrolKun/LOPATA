using System.IO.Compression;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VisualScenario;
using SkiaSharp;
using AIHub.Controls;
using AIHub.Controls.VisualScenario;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class ImageGenerationTests
{
    private string _root = null!;
    [TestInitialize] public void Initialize() => _root = Path.Combine(Path.GetTempPath(), "lopata-generation-" + Guid.NewGuid().ToString("N"));
    [TestCleanup] public void Cleanup()
    {
        var root = Path.GetFullPath(_root);
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), root);
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
    private ImageGenerationRequest Request(string model = "z-image") => new(Guid.NewGuid().ToString("N"), model,
        "  Кот говорит «Привет»\n--steps 999 🐈  ", 256, 256, [41, 42], Path.Combine(_root, "models"), ImageGenerationSessionStore.Create(_root));

    [TestMethod]
    public async Task VisualDrawingCanResumeASavedJobWithoutASessionFile()
    {
        var projects = new VisualProjects(Path.Combine(_root, "visual"));
        var project = new VisualProject { Persistent = true, Artist = VisualComponents.Turbo };
        projects.Save(project);
        var directory = projects.DirectoryFor(project.Id);
        var request = new ImageGenerationRequest(Guid.NewGuid().ToString("N"), project.Artist, "Approved prompt",
            1024, 576, [41], Path.Combine(_root, "models"), directory) { EngineSetId = NativeRuntimeReleasePolicy.UpdatedSet };
        var checkpoint = JsonSerializer.SerializeToElement(new VisualJob(project.Id, new(request.Prompt, []), request));
        var job = checkpoint.Deserialize<VisualJob>()!;
        Assert.IsFalse(File.Exists(Path.Combine(directory, "session.json")));

        var worker = new Worker(job.Prompt.Text);
        var runtime = new ImageGenerationRuntime(worker);
        projects.EnsureDrawingSession(job);
        var turn = await runtime.RunAsync(job.Request, [], null, CancellationToken.None);
        Assert.HasCount(1, turn.Results);
        Assert.IsTrue(File.Exists(ImageGenerationSessionStore.ResultPath(job.Request, 0)));

        projects.EnsureDrawingSession(job);
        var restored = await runtime.RunAsync(job.Request, [], null, CancellationToken.None);
        Assert.AreEqual(turn.Results[0], restored.Results[0]);
        CollectionAssert.AreEqual(new[] { 0 }, worker.Calls.ToArray());
        var session = ImageGenerationSessionStore.Load(directory);
        Assert.AreEqual(project.Id, session.Id);
        Assert.HasCount(1, session.Turns);
        Assert.AreEqual(request.Prompt, session.Turns[0].Request.Prompt);
        Assert.AreEqual(project.Id, projects.Current()!.Id);
    }

    [TestMethod]
    public void ManifestPinsEveryArtifactAndKeepsNavigationResolvable()
    {
        CollectionAssert.AreEqual(new[] { "z-image", "krea", "qwen-image-21-turbo-int8", "qwen-image-21-base-gguf", MiniMaxImageRuntime.ModelId },
            ImageGenerationCatalog.Manifest.Models.Select(m => m.Id).ToArray());
        Assert.AreEqual(14, ImageGenerationCatalog.Manifest.Artifacts.Length);
        var licenseRoot = Path.Combine(AppContext.BaseDirectory, "Licenses");
        var licenseService = new ComponentLicenseService(licenseRoot, Path.Combine(_root, "licenses.json"));
        foreach (var artifact in ImageGenerationCatalog.Manifest.Artifacts)
            Assert.IsTrue(ImageGenerationCatalog.Manifest.Models.Any(m => m.Components.Contains(artifact.Id)));
        foreach (var artifact in ImageGenerationCatalog.Manifest.Artifacts)
        foreach (var file in artifact.Files)
        {
            Assert.AreEqual(64, file.Sha256.Length); Assert.IsTrue(file.Sha256.All(Uri.IsHexDigit));
            Assert.IsTrue(file.SourceUrl.Contains(artifact.Revision, StringComparison.Ordinal));
            var license = licenseService.Entries.Single(entry => entry.Id == artifact.Id);
            foreach (var text in license.Texts)
                Assert.IsFalse(string.IsNullOrWhiteSpace(licenseService.ReadText(text)));
        }
        var entrance = ScenarioNavigationCatalog.Get(ScenarioNavigationCatalog.ImageGeneration);
        Assert.AreEqual("creation_images", entrance.ParentId);
        Assert.AreEqual(ScenarioNavigationCatalog.Creation, ScenarioNavigationCatalog.Get(entrance.ParentId!).ParentId);
        Assert.AreEqual(entrance.Id, ScenarioNavigationCatalog.GetTag("image_generation").TargetId);
        foreach (var language in new[] { "ru", "en" })
        {
            var l = new LocalizationService(); l.Load(language);
            Assert.AreNotEqual("Generation.Title", l.T("Generation.Title"));
            foreach (var model in ImageGenerationCatalog.Manifest.Models)
            {
                var key = "Generation.Model." + (model.Family ?? model.Id);
                Assert.AreNotEqual(key, l.T(key));
            }
        }
    }

    [TestMethod]
    [DataRow("vulkan", true)]
    [DataRow("cpu", false)]
    public void MiniMaxUsesDedicatedRuntimeAndPinnedSamplingProfile(string backend, bool usesGpu)
    {
        var request = Request(MiniMaxImageRuntime.ModelId) with { Width = 4096, Height = 4096 };
        var model = ImageGenerationCatalog.Get(request.ModelId);
        ImageGenerationCatalog.Validate(request);
        Assert.IsFalse(ImageGenerationCatalog.VideoReferenceModels.Contains(model));
        var cards = ImageGenerationCatalog.CreateCards(request.ModelsRoot).Where(card => model.Components.Contains(card.ModelArtifactId)).ToArray();
        var runtime = cards.Single(card => card.ModelArtifactId == MiniMaxImageRuntime.ArtifactId);
        var weights = cards.Single(card => card.ModelArtifactId != runtime.ModelArtifactId);
        var directory = MiniMaxImageRuntime.BackendDirectory(runtime, backend);
        var selection = new SdRuntimeSelection(runtime.ModelArtifactId, directory, usesGpu)
        { Backend = backend, PackageRevision = MiniMaxImageRuntime.Commit, PackageVersion = runtime.Revision };
        var command = ImageGenerationNativeWorker.ManagedCommand(request, 0, cards, "raw input.txt", "out.png", selection);
        var args = command.ArgumentList;
        string Value(string flag) => args[args.IndexOf(flag) + 1];
        Assert.AreEqual(Path.Combine(directory, "sd-cli.exe"), command.FileName);
        Assert.AreEqual("minimax-h3-image", Value("--sigmas"));
        Assert.AreEqual("6", Value("--steps"));
        Assert.AreEqual("1", Value("--cfg-scale"));
        Assert.AreEqual("euler", Value("--sampling-method"));
        Assert.AreEqual("on", Value("--auto-fit"));
        Assert.AreEqual("raw input.txt", Value("--prompt-file"));
        Assert.AreEqual(49_289_162_266L, weights.TotalBytes);
        foreach (var (flag, purpose) in new[] { ("--diffusion-model", "diffusion"), ("--llm", "llm"), ("--vae", "vae") })
            Assert.AreEqual(Path.Combine(weights.InstallDirectory, weights.Files.Single(file => file.Purpose == purpose).RelativePath), Value(flag));
        if (usesGpu) Assert.AreEqual("-1", Value("--max-vram"));
        else Assert.AreEqual("cpu", Value("--backend"));
        Assert.IsFalse(args.Contains("--diffusion-fa"));
        var server = ImageGenerationNativeWorker.ManagedCommand(request, 0, cards, "raw input.txt", "out.png", selection, server: true);
        var repeat = ImageGenerationNativeWorker.ManagedCommand(request with { Prompt = "Another prompt", Seeds = [123] },
            0, cards, "another input.txt", "another.png", selection, server: true);
        Assert.AreEqual(Path.Combine(directory, "sd-server.exe"), server.FileName);
        Assert.AreEqual((1024, 1024), ImageGenerationResident.WarmupSize(model));
        Assert.AreEqual("1024", server.ArgumentList[server.ArgumentList.IndexOf("--width") + 1]);
        Assert.AreEqual("1024", server.ArgumentList[server.ArgumentList.IndexOf("--height") + 1]);
        var warmupServer = ImageGenerationNativeWorker.ManagedCommand(
            request with { Width = model.DefaultWidth, Height = model.DefaultHeight },
            0, cards, "", "", selection, server: true);
        CollectionAssert.AreEqual(server.ArgumentList.ToArray(), warmupServer.ArgumentList.ToArray());
        if (usesGpu)
            Assert.AreEqual("-4", server.ArgumentList[server.ArgumentList.IndexOf("--max-vram") + 1]);
        else
            Assert.IsFalse(server.ArgumentList.Contains("--max-vram"));
        Assert.DoesNotContain("--prompt-file", server.ArgumentList);
        Assert.DoesNotContain("--output", server.ArgumentList);
        CollectionAssert.AreEqual(server.ArgumentList.ToArray(), repeat.ArgumentList.ToArray());
    }

    [TestMethod]
    public void MiniMaxRemainingBytesIncludeCompletedFilesAndPartialDownloads()
    {
        var model = ImageGenerationCatalog.Get(MiniMaxImageRuntime.ModelId);
        var cards = ImageGenerationCatalog.CreateCards(_root, new ModelStorageAddresses(_root))
            .Where(card => model.Components.Contains(card.ModelArtifactId)).ToArray();
        var weights = cards.Single(card => card.ModelArtifactId != MiniMaxImageRuntime.ArtifactId);
        var vae = weights.Files.Single(file => file.Purpose == "vae");
        var path = Path.Combine(weights.InstallDirectory, vae.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path + ".part", new byte[128]);
        cards.Single(card => card.ModelArtifactId == MiniMaxImageRuntime.ArtifactId).Status = ManagedModelStatuses.Installed;
        Assert.AreEqual(weights.TotalBytes - 128, MiniMaxImageRuntime.RemainingDownloadBytes(cards));
    }

    [TestMethod]
    public void ResidentOnlyRejectsCpuOrDiskPlacementForGpuRequiredProfiles()
    {
        var miniMax = ImageGenerationCatalog.Get(MiniMaxImageRuntime.ModelId);
        foreach (var placement in new[] { ImageGenerationGpuPlacement.GpuUnavailable, ImageGenerationGpuPlacement.MemoryUnavailable })
        {
            Assert.IsFalse(ImageGenerationResident.RejectsPlacement(miniMax.RequiresGpu, placement));
            Assert.IsTrue(ImageGenerationResident.RejectsPlacement(true, placement));
        }
        Assert.IsFalse(ImageGenerationResident.RejectsPlacement(true, ImageGenerationGpuPlacement.Hybrid));
    }
    [TestMethod]
    [DataRow("z-image", 8, "1")]
    [DataRow("krea", 8, "1")]
    public void NativeCommandsSelectTheCorrectArchitectureAndPreserveRawPrompt(string id, int steps, string cfg)
    {
        var request = Request(id);
        var components = ImageGenerationCatalog.Get(id).Components;
        var cards = ImageGenerationCatalog.CreateCards(request.ModelsRoot).Where(c => components.Contains(c.ModelArtifactId)).ToArray();
        var directory = Path.Combine(cards.Single(c => c.ModelArtifactId == "generation-runtime").InstallDirectory, "bin");
        Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "sd-cli.exe"), "stub");
        var command = ImageGenerationNativeWorker.Command(request, 1, cards, "raw input.txt", "out.png");
        var args = command.ArgumentList.ToArray();
        string Value(string flag) => args[Array.IndexOf(args, flag) + 1];
        Assert.AreEqual(steps.ToString(), Value("--steps")); Assert.AreEqual(cfg, Value("--cfg-scale"));
        Assert.AreEqual("42", Value("--seed")); Assert.AreEqual("raw input.txt", Value("--prompt-file"));
        Assert.IsFalse(args.Contains(request.Prompt)); Assert.IsFalse(args.Contains("--negative-prompt"));
        Assert.IsFalse(args.Contains("--model"));
        Assert.IsTrue(args.Contains("--llm"));
        Assert.IsFalse(command.UseShellExecute);
        Assert.IsFalse(args.Contains("--clip-skip"));
        if (id == "krea") Assert.AreEqual("1.15", Value("--flow-shift"));

        var runtime = new SdRuntimeSelection("test", directory, true)
            { EngineSetId = NativeRuntimeReleasePolicy.UpdatedSet, Backend = "vulkan" };
        Assert.IsFalse(ImageGenerationNativeWorker.CanUseServer(request, runtime));
        File.WriteAllText(Path.Combine(directory, "sd-server.exe"), "stub");
        Assert.IsTrue(ImageGenerationNativeWorker.CanUseServer(request, runtime));
        Assert.AreEqual((256, 256), ImageGenerationResident.WarmupSize(ImageGenerationCatalog.Get(id)));
        var server = ImageGenerationNativeWorker.ManagedCommand(request, 0, cards, "", "", runtime, server: true);
        var largerServer = ImageGenerationNativeWorker.ManagedCommand(request with { Width = 2048, Height = 2048 },
            1, cards, "other input.txt", "other.png", runtime, server: true);
        CollectionAssert.AreEqual(server.ArgumentList.ToArray(), largerServer.ArgumentList.ToArray());
    }

    [TestMethod]
    [DataRow("qwen-image-21-turbo-int8", 8, "1", "qwen_image_2.1_turbo_int8_convrot.safetensors")]
    [DataRow("qwen-image-21-base-gguf", 40, "6", "qwen_image_2.1-Q4_K.gguf")]
    public void QwenCommandsSelectPinnedComponentsAndAutomaticPlacement(string id, int steps, string cfg, string diffusion)
    {
        var request = Request(id);
        var model = ImageGenerationCatalog.Get(id);
        Assert.IsTrue(model.RequiresGpu);
        Assert.IsFalse(ImageGenerationCatalog.SupportsEngineSet(model, NativeRuntimeReleasePolicy.LegacySet));
        var selection = new SdRuntimeSelection("test", _root, true) { EngineSetId = NativeRuntimeReleasePolicy.UpdatedSet, Backend = "vulkan" };
        var cards = ImageGenerationCatalog.CreateCards(request.ModelsRoot).Where(card => model.Components.Contains(card.ModelArtifactId)).ToArray();
        var args = ImageGenerationNativeWorker.ManagedCommand(request, 0, cards, "raw input.txt", "out.png", selection).ArgumentList.ToArray();
        string Value(string flag) => args[Array.IndexOf(args, flag) + 1];
        Assert.AreEqual(diffusion, Path.GetFileName(Value("--diffusion-model")));
        Assert.AreEqual(steps.ToString(), Value("--steps"));
        Assert.AreEqual(cfg, Value("--cfg-scale"));
        Assert.AreEqual("euler", Value("--sampling-method"));
        Assert.AreEqual("on", Value("--auto-fit"));
        Assert.IsFalse(args.Contains("--backend") || args.Contains("--offload-to-cpu"));
        Assert.IsFalse(args.Contains("--max-vram"));
        Assert.IsFalse(args.Contains("--preview"));
        var preview = Path.Combine(_root, "frames", "frame_%03d.png");
        var previewArgs = ImageGenerationNativeWorker.ManagedCommand(request, 0, cards, "raw input.txt", "out.png",
            selection, preview).ArgumentList.ToList();
        var previewStart = previewArgs.IndexOf("--preview");
        CollectionAssert.AreEqual(new[] { "--preview", "proj", "--preview-interval", "1", "--preview-path", preview },
            previewArgs.GetRange(previewStart, 6));
        previewArgs.RemoveRange(previewStart, 6);
        CollectionAssert.AreEqual(args, previewArgs.ToArray());
        var larger = ImageGenerationNativeWorker.ManagedCommand(request with { Width = 2048, Height = 2048 },
            0, cards, "raw input.txt", "out.png", selection).ArgumentList.ToArray();
        Assert.AreEqual("-1", larger[Array.IndexOf(larger, "--max-vram") + 1]);
        Assert.AreEqual((1024, 1024), ImageGenerationResident.WarmupSize(model));
        var server = ImageGenerationNativeWorker.ManagedCommand(request with { Width = 1024, Height = 1024 },
            0, cards, "", "", selection, server: true);
        var largerServer = ImageGenerationNativeWorker.ManagedCommand(request with { Width = 2048, Height = 2048 },
            1, cards, "other input.txt", "other.png", selection, server: true);
        CollectionAssert.AreEqual(server.ArgumentList.ToArray(), largerServer.ArgumentList.ToArray());
        Assert.AreEqual("-1", server.ArgumentList[server.ArgumentList.IndexOf("--max-vram") + 1]);
        if (steps == 8) Assert.AreEqual("qwen-image-2.1-turbo", Value("--sigmas"));
        else Assert.IsFalse(args.Contains("--sigmas"));
        var expectedEncoder = cards.SelectMany(card => card.Files).Single(file => file.Purpose == "llm");
        Assert.AreEqual(expectedEncoder.RelativePath, Path.GetFileName(Value("--llm")));
    }

    [TestMethod]
    public void QwenPreparesLargeReferencesInOrderAndRetainsTheOriginalFiles()
    {
        var request = Request("qwen-image-21-turbo-int8");
        var colors = new[] { SKColors.CornflowerBlue, SKColors.Green, SKColors.Tomato };
        var sources = colors.Select((color, index) =>
        {
            var path = Path.Combine(_root, $"reference-{index}.png");
            var size = index == 1 ? 256 : 1536;
            using var bitmap = new SKBitmap(size, size);
            bitmap.Erase(color);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = File.Create(path);
            data.SaveTo(stream);
            return path;
        }).ToArray();

        Assert.IsFalse(ImageGenerationReferences.NeedsPreparation(request with { ReferenceImages = [sources[0]] }));
        request = request with { ReferenceImages = sources };
        Assert.IsTrue(ImageGenerationReferences.NeedsPreparation(request));
        var paths = Array.Empty<string>();
        using (var references = ImageGenerationReferences.Prepare(request.ReferenceImages, CancellationToken.None))
        {
            paths = references.Paths;
            Assert.AreEqual(sources[1], paths[1]);
            Assert.AreNotEqual(sources[0], paths[0]);
            Assert.AreNotEqual(sources[2], paths[2]);
            for (var index = 0; index < paths.Length; index++)
            {
                using var bitmap = SKBitmap.Decode(paths[index]);
                Assert.AreEqual(index == 1 ? 256 : 1024, bitmap.Width);
                Assert.AreEqual(colors[index], bitmap.GetPixel(0, 0));
            }
        }
        Assert.IsFalse(File.Exists(paths[0]));
        Assert.IsFalse(File.Exists(paths[2]));
        Assert.IsTrue(sources.All(File.Exists));
        using var original = SKBitmap.Decode(sources[0]);
        Assert.AreEqual(1536, original.Width);
    }

    [TestMethod]
    public void LivePreviewKeepsTheLastCompleteFrameAndCleansAttemptFiles()
    {
        using var preview = ImageGenerationLivePreview.TryCreate();
        Assert.IsNotNull(preview);
        var directory = Path.GetDirectoryName(preview.OutputPattern)!.Replace("%%", "%");
        var first = Path.Combine(directory, "frame_000.png");
        var next = Path.Combine(directory, "frame_002.png");
        WriteFrame(first, 4);
        var frame = preview.ReadLatest();
        Assert.IsNotNull(frame);
        Assert.IsTrue(frame.IsFrozen);
        Assert.AreEqual(4, frame.PixelWidth);
        Assert.IsNull(preview.ReadLatest());
        File.WriteAllText(next, "PNG still being written");
        Assert.IsNull(preview.ReadLatest());
        WriteFrame(next, 8);
        Assert.AreEqual(8, preview.ReadLatest()?.PixelWidth);
        Assert.IsNull(preview.ReadLatest());
        preview.Dispose();
        Assert.IsFalse(Directory.Exists(directory));

        static void WriteFrame(string path, int size)
        {
            using var bitmap = new SKBitmap(size, size);
            bitmap.Erase(SKColors.CornflowerBlue);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = File.Create(path);
            data.SaveTo(stream);
        }
    }

    [TestMethod]
    [DataRow(ImageGenerationResident.Interrupted, true)]
    [DataRow("HttpRequestException", true)]
    [DataRow("Generation.Qwen.MemoryUnavailable", false)]
    [DataRow(null, false)]
    public void OnlyResidentTransportInterruptionSelectsStandaloneRecovery(string? detail, bool standalone) =>
        Assert.AreEqual(standalone, ImageGenerationResident.RequiresStandalone(detail));

    [TestMethod]
    public async Task ActualVisualScenarioShowsQwenPreviewAndReplacesItWithFinalResult()
    {
        var models = Environment.GetEnvironmentVariable("AIHUB_IMAGE_NATIVE_MODEL_ROOT");
        if (string.IsNullOrWhiteSpace(models)) Assert.Inconclusive("An explicit existing model directory is required.");
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            _ = dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    using var audit = ComponentAudit.UseTestStore(Path.Combine(_root, "audit"), enabled: false);
                    var projects = new VisualProjects(Path.Combine(_root, "projects"));
                    var project = new VisualProject
                    { Artist = VisualComponents.Turbo, Persistent = true, OutputFormat = ImageGenerationFileFormat.WebP };
                    projects.Save(project);
                    var view = new VisualScenarioControl(key => key, projects);
                    var storage = new StorageSettings { Models = new() { Locations = [new() { Path = models }] } };
                    view.Configure(key => key, new(storage, 1), null);
                    view.Measure(new Size(1200, 760));
                    view.Arrange(new Rect(0, 0, 1200, 760));
                    var preview = Descendants(view).OfType<System.Windows.Controls.Image>()
                        .Single(image => AutomationProperties.GetAutomationId(image) == "Visual.LivePreview");
                    var frames = new HashSet<ImageSource>();
                    double? previewX = null;
                    var observer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                    observer.Tick += (_, _) =>
                    {
                        if (preview.Source is not { } frame || preview.Visibility != Visibility.Visible) return;
                        if (((StackPanel)preview.Parent).Visibility != Visibility.Visible) return;
                        frames.Add(frame);
                        view.UpdateLayout();
                        previewX ??= preview.TranslatePoint(new Point(0, 0), view).X;
                    };
                    var prompt = new VisualPrompt("A small brass compass on a blue tabletop.", []);
                    var request = new ImageGenerationRequest(Guid.NewGuid().ToString("N"), project.Artist,
                        prompt.Text, 1024, 1024, [424242], models, projects.DirectoryFor(project.Id))
                    {
                        EngineSetId = NativeRuntimeReleasePolicy.UpdatedSet,
                        OutputFormat = project.OutputFormat,
                        Metadata = new("Test author", "image-generation/visual-scenario"),
                        OutputFolder = Path.Combine(_root, "export")
                    };
                    var job = new VisualJob(project.Id, prompt, request);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                    observer.Start();
                    try
                    {
                        await view.RestoreAsync(new() { Kind = VisualScenarioControl.DrawingKind, Title = "Test",
                            Project = project.Id, Input = JsonSerializer.SerializeToElement(job) }, timeout.Token);
                    }
                    finally { observer.Stop(); }
                    Assert.IsGreaterThanOrEqualTo(2, frames.Count);
                    Assert.IsGreaterThan(600, previewX.GetValueOrDefault());
                    Assert.IsNull(preview.Source);
                    Assert.AreEqual(Visibility.Collapsed, ((StackPanel)preview.Parent).Visibility);
                    var result = projects.Load(project.Id).Results.Single();
                    Assert.IsTrue(File.Exists(result.Result));
                    Assert.AreEqual(".png", Path.GetExtension(result.Result));
                    var exported = Directory.GetFiles(request.OutputFolder).Single();
                    Assert.AreEqual(".webp", Path.GetExtension(exported));
                    Assert.AreEqual(prompt.Text, PngTextMetadata.Read(result.Result)["Prompt"]);
                    if (Environment.GetEnvironmentVariable("AIHUB_ENGINE_NATIVE_OUTPUT") is { Length: > 0 } output)
                    {
                        Directory.CreateDirectory(output);
                        File.Copy(exported, Path.Combine(output, "visual-scenario.webp"), true);
                    }

                    CheckBox RepeatSwitch() => Descendants(view).OfType<CheckBox>().Single(box =>
                        AutomationProperties.GetAutomationId(box).EndsWith(".Repeat", StringComparison.Ordinal));
                    IReadOnlyList<OwnedProcessRegistry.Snapshot> ArtistProcesses() =>
                        OwnedProcessRegistry.Shared.GetSnapshot().Where(process => process.Component == "ImageGeneration").ToArray();
                    RepeatSwitch().IsChecked = true;
                    frames.Clear();
                    observer.Start();
                    using var repeatTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                    try
                    {
                        int? serverPid = null;
                        foreach (var variant in new long[] { 424243, 424244 })
                        {
                            var repeat = request with { Id = Guid.NewGuid().ToString("N"), Width = 512, Height = 512, Seeds = [variant] };
                            await view.RestoreAsync(new() { Kind = VisualScenarioControl.DrawingKind, Title = "Test",
                                Project = project.Id, Input = JsonSerializer.SerializeToElement(job with { Request = repeat }) }, repeatTimeout.Token);
                            var process = ArtistProcesses().Single();
                            serverPid ??= process.Pid;
                            Assert.AreEqual(serverPid.Value, process.Pid);
                            var latest = projects.Load(project.Id).Results.Last();
                            Assert.AreEqual(prompt.Text, PngTextMetadata.Read(latest.Result)["Prompt"]);
                        }
                        Assert.IsGreaterThanOrEqualTo(2, frames.Count);

                        var interruptedRequest = request with { Id = Guid.NewGuid().ToString("N"), Seeds = [424245] };
                        var interruptedJob = new BackgroundOperationState
                        {
                            Kind = VisualScenarioControl.DrawingKind, Title = "Repeat recovery", Project = project.Id,
                            Input = JsonSerializer.SerializeToElement(job with { Request = interruptedRequest })
                        };
                        var previousController = ApplicationBackgroundOperations.Current;
                        var controller = new BackgroundOperationController(new(Path.Combine(_root, "operation.json")));
                        controller.Register(interruptedJob.Kind, view.RestoreAsync);
                        ApplicationBackgroundOperations.Current = controller;
                        try
                        {
                            frames.Clear();
                            using var recoveryTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                            var running = view.RestoreAsync(interruptedJob, recoveryTimeout.Token);
                            while (frames.Count == 0 && !running.IsCompleted) await Task.Delay(50, recoveryTimeout.Token);
                            Assert.IsFalse(running.IsCompleted, "The test must interrupt an in-flight draw.");
                            Assert.AreEqual(serverPid, ArtistProcesses().Single().Pid);
                            using (var worker = System.Diagnostics.Process.GetProcessById(serverPid!.Value)) worker.Kill();
                            await Assert.ThrowsAsync<BackgroundOperationWaitingException>(() => running);
                            Assert.AreEqual(ImageGenerationResident.Interrupted, controller.State!.Detail);
                            Assert.IsTrue(File.Exists(Path.Combine(request.SessionDirectory, interruptedRequest.Id + "_0.resident-error.log")));
                            await controller.ResumeAsync(interruptedJob.Id, recoveryTimeout.Token);
                            Assert.AreEqual(BackgroundOperationPhase.Completed, controller.State!.Phase);
                            Assert.IsTrue(controller.State.Input.GetProperty(nameof(VisualJob.Standalone)).GetBoolean());
                            var recovered = projects.Load(project.Id).Results.Single(item => item.Id == interruptedRequest.Id);
                            Assert.AreEqual(prompt.Text, PngTextMetadata.Read(recovered.Result)["Prompt"]);
                            Assert.IsEmpty(ArtistProcesses());
                        }
                        finally { ApplicationBackgroundOperations.Current = previousController; }

                        var next = request with { Id = Guid.NewGuid().ToString("N"), Width = 512, Height = 512, Seeds = [424246] };
                        await view.RestoreAsync(new() { Kind = VisualScenarioControl.DrawingKind, Title = "Test",
                            Project = project.Id, Input = JsonSerializer.SerializeToElement(job with { Request = next }) }, repeatTimeout.Token);
                        Assert.HasCount(1, ArtistProcesses());
                        RepeatSwitch().IsChecked = false;
                        using var retireTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        while (ArtistProcesses().Count > 0) await Task.Delay(50, retireTimeout.Token);
                    }
                    finally
                    {
                        observer.Stop();
                        await ApplicationBackgroundOperations.RetireModelsAsync();
                    }
                    Assert.IsEmpty(ArtistProcesses());
                    Assert.HasCount(5, projects.Load(project.Id).Results);
                    complete.SetResult();
                }
                catch (Exception error) { complete.SetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await complete.Task.WaitAsync(TimeSpan.FromMinutes(6));
    }

    [TestMethod]
    [DataRow("qwen-image-21-turbo-full")]
    [DataRow("qwen-image-21-turbo-gguf")]
    [DataRow("qwen-image-21-base-int8")]
    public void RetiredQwenHistoryKeepsTheDisplayNameAndCannotRedownload(string modelId)
    {
        var store = new ManagedModelLibraryStore(_root);
        var artifactId = "generation-" + modelId;
        store.Upsert(new ManagedModelArtifactCard { ModelArtifactId = artifactId, SupportsDirectDownload = true });
        Assert.IsFalse(ImageGenerationCatalog.IsAvailable(modelId));
        Assert.StartsWith("Qwen-Image-2.1 ", ImageGenerationCatalog.DisplayName(modelId));
        Assert.IsFalse(store.Load(artifactId)!.SupportsDirectDownload);
    }

    [TestMethod]
    [DataRow("auto-fit: --backend \"diffusion=CUDA0,te=CUDA0,vae=CUDA0\"", "Vram")]
    [DataRow("auto-fit: --backend \"diffusion=Vulkan0,te=Vulkan0,vae=Vulkan0\" --params-backend \"te=cpu\"", "Hybrid")]
    [DataRow("auto-fit: --backend \"diffusion=Vulkan0,te=Vulkan0\" --params-backend \"te=disk\"", "MemoryUnavailable")]
    [DataRow("auto-fit: no GPU memory budget available; using CPU", "MemoryUnavailable")]
    [DataRow("auto-fit: no GPU devices; using the default backend", "GpuUnavailable")]
    [DataRow("auto-fit: --backend \"diffusion=cpu,te=Vulkan0\"", "GpuUnavailable")]
    public void QwenPlacementUsesNativePlan(string log, string expected) =>
        Assert.AreEqual(expected, ImageGenerationPlacementLog.Read(log).ToString());

    [TestMethod]
    public void QwenQuantizationsShareComponents()
    {
        var models = ImageGenerationCatalog.Manifest.Models.Where(model => model.Family is not null).ToArray();
        Assert.HasCount(2, models);
        CollectionAssert.AreEqual(new[] { "generation-qwen-image-21-vae" },
            models[0].Components.Intersect(models[1].Components).ToArray());
        Assert.Contains("generation-qwen-image-21-text-int8", models[0].Components);
        Assert.Contains("generation-qwen-image-21-text-gguf", models[1].Components);
    }
    [TestMethod]
    [DataRow("z-image")]
    [DataRow(MiniMaxImageRuntime.ModelId)]
    public async Task CancellationRetainsFinishedImagesAndResumeDoesNotGenerateThemAgain(string modelId)
    {
        var request = Request(modelId); var worker = new Worker(request.Prompt) { CancelAt = 1 };
        var runtime = new ImageGenerationRuntime(worker);
        await Assert.ThrowsAsync<OperationCanceledException>(() => runtime.RunAsync(request, [], null, CancellationToken.None));
        var saved = ImageGenerationSessionStore.Load(request.SessionDirectory);
        Assert.HasCount(1, saved.Turns); Assert.HasCount(1, saved.Turns[0].Results);
        var firstPath = ImageGenerationSessionStore.ResultPath(request, 0); var bytes = File.ReadAllBytes(firstPath);
        worker.CancelAt = -1;
        var restored = JsonSerializer.Deserialize<ImageGenerationRequest>(JsonSerializer.Serialize(request))!;
        var result = await new ImageGenerationRuntime(worker).RunAsync(restored, [], null, CancellationToken.None);
        Assert.HasCount(2, result.Results);
        CollectionAssert.AreEqual(new[] { 0, 1, 1 }, worker.Calls.ToArray());
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(firstPath));
        Assert.AreEqual(request.Prompt, File.ReadAllText(Path.Combine(request.SessionDirectory, request.Id + ".input.txt")));
        // A completed replay also does not duplicate results.
        await runtime.RunAsync(request, [], null, CancellationToken.None);
        Assert.HasCount(3, worker.Calls); Assert.HasCount(1, ImageGenerationSessionStore.Load(request.SessionDirectory).Turns);
    }
    [TestMethod]
    public async Task InvalidNativeOutputIsNeverPublishedAsAResult()
    {
        var request = Request();
        var worker = new Worker(request.Prompt) { Invalid = true };
        var error = await Assert.ThrowsAsync<BackgroundOperationFailedException>(() => new ImageGenerationRuntime(worker).RunAsync(request, [], null, CancellationToken.None));
        Assert.AreEqual("Generation.InvalidImage", error.Message);
        Assert.IsFalse(File.Exists(ImageGenerationSessionStore.ResultPath(request, 0)));
        Assert.HasCount(0, ImageGenerationSessionStore.Load(request.SessionDirectory).Turns[0].Results);
    }
    [TestMethod]
    public void RuntimeArchivesCannotEscapeTheirDirectory()
    {
        Directory.CreateDirectory(_root); var path = Path.Combine(_root, "bad.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("../outside.txt").Open())) writer.Write("bad");
        Assert.Throws<InvalidDataException>(() => ImageGenerationInstallation.ExtractArchive(path, Path.Combine(_root, "bin"), CancellationToken.None));
        Assert.IsFalse(File.Exists(Path.Combine(_root, "outside.txt")));
    }
    [TestMethod]
    public void ChangedModelStorageDoesNotReuseAnotherRootsVerification()
    {
        using var installer = new ImageGenerationInstallation(new ManagedModelLibraryStore(Path.Combine(_root, "library")));
        var first = installer.Register(Path.Combine(_root, "first"), "krea");
        var second = installer.Register(Path.Combine(_root, "second"), "krea");
        Assert.AreNotEqual(first[0].InstallDirectory, second[0].InstallDirectory);
        Assert.IsFalse(installer.IsReady(Path.Combine(_root, "second"), "krea"));
        Assert.IsTrue(second.All(c => c.Files.All(f => f.VerifiedLastWriteTimeUtc is null)));
    }
    [TestMethod]
    public Task ModelSelectionDoesNotDownloadAndTheChatDisplaysImagesInline()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var localizer = new LocalizationService(); localizer.Load("ru");
                using var installer = new ImageGenerationInstallation(new ManagedModelLibraryStore(Path.Combine(_root, "library")));
                var control = new ImageGenerationControl(installer, new());
                control.Configure(localizer.T, new StorageSettings { Models = new() { Locations = [new() { Path = Path.Combine(_root, "models") }] } }, 4);
                var selector = Descendants(control).OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "Generation.Model.krea");
                selector.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.IsFalse(Directory.Exists(Path.Combine(_root, "models")));
                Assert.IsFalse(Descendants(control).OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "Generation.OpenChat").IsEnabled);
                Assert.IsTrue(control.GoBack()); Assert.IsFalse(control.GoBack());
                var request = Request("krea"); ImageGenerationSessionStore.AddTurn(request);
                var imagePath = ImageGenerationSessionStore.ResultPath(request, 0);
                new Worker(request.Prompt).GenerateAsync(request, 0, [], WritePrompt(request), imagePath, CancellationToken.None).GetAwaiter().GetResult();
                ImageGenerationSessionStore.PutResult(request, new(0, Path.GetFileName(imagePath), request.Seeds[0]));
                control.Restore(request.SessionDirectory);
                Assert.HasCount(1, Descendants(control).OfType<System.Windows.Controls.Image>().ToArray());
                var source = (System.Windows.Media.Imaging.BitmapSource)Descendants(control).OfType<System.Windows.Controls.Image>().Single().Source;
                Assert.AreEqual(256, source.PixelWidth);
                // Old results stay viewable after their model is removed, without enabling a new variant.
                var oldRequest = Request("pony"); ImageGenerationSessionStore.AddTurn(oldRequest);
                var oldImage = ImageGenerationSessionStore.ResultPath(oldRequest, 0);
                new Worker(oldRequest.Prompt).GenerateAsync(oldRequest, 0, [], WritePrompt(oldRequest), oldImage, CancellationToken.None).GetAwaiter().GetResult();
                ImageGenerationSessionStore.PutResult(oldRequest, new(0, Path.GetFileName(oldImage), oldRequest.Seeds[0]));
                control.Restore(oldRequest.SessionDirectory);
                Assert.HasCount(1, Descendants(control).OfType<System.Windows.Controls.Image>().ToArray());
                Assert.IsFalse(Descendants(control).OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "Generation.Another.0").IsEnabled);
                Assert.IsTrue(File.Exists(oldImage));
                control.Measure(new Size(1000, 750)); control.Arrange(new Rect(0, 0, 1000, 750));
                Assert.IsTrue(control.ActualWidth > 0); done.SetResult();
            }
            catch (Exception error) { done.SetException(error); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task;
    }
    [TestMethod]
    [DataRow("noobai")]
    [DataRow("pony")]
    [DataRow("flux-klein")]
    public void RemovedModelsCannotBeDownloadedOrGenerated(string id)
    {
        Assert.IsFalse(ImageGenerationCatalog.IsAvailable(id));
        var error = Assert.Throws<InvalidOperationException>(() => ImageGenerationCatalog.Validate(Request(id)));
        Assert.AreEqual("Generation.ModelUnavailable", error.Message);
        using var installer = new ImageGenerationInstallation(new ManagedModelLibraryStore(Path.Combine(_root, "library")));
        Assert.Throws<InvalidOperationException>(() => installer.Register(Path.Combine(_root, "models"), id));
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, "models")));
    }
    private static string WritePrompt(ImageGenerationRequest request)
    {
        var path = Path.Combine(request.SessionDirectory, "test-input.txt"); File.WriteAllText(path, request.Prompt); return path;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        foreach (var descendant in Descendants(child)) yield return descendant;
    }
    private sealed class Worker(string prompt) : IImageGenerationWorker
    {
        public List<int> Calls { get; } = [];
        public int CancelAt { get; set; } = -1;
        public bool Invalid { get; set; }
        public Task GenerateAsync(ImageGenerationRequest request, int index, IReadOnlyList<ManagedModelArtifactCard> cards,
            string promptFile, string output, CancellationToken token)
        {
            Calls.Add(index); Assert.AreEqual(prompt, File.ReadAllText(promptFile));
            if (index == CancelAt) throw new OperationCanceledException();
            if (Invalid) File.WriteAllText(output, "not an image");
            else
            {
                using var bitmap = new SKBitmap(request.Width, request.Height); bitmap.Erase(SKColors.CornflowerBlue);
                using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                using var stream = File.Create(output); data.SaveTo(stream);
            }
            return Task.CompletedTask;
        }
    }
}
