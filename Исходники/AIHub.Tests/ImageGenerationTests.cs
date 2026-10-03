using System.IO.Compression;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using SkiaSharp;
using AIHub.Controls;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

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
    public void ManifestPinsEveryArtifactAndKeepsNavigationResolvable()
    {
        CollectionAssert.AreEqual(new[] { "z-image", "krea" }, ImageGenerationCatalog.Manifest.Models.Select(m => m.Id).ToArray());
        Assert.AreEqual(7, ImageGenerationCatalog.Manifest.Artifacts.Length);
        foreach (var artifact in ImageGenerationCatalog.Manifest.Artifacts)
            Assert.IsTrue(ImageGenerationCatalog.Manifest.Models.Any(m => m.Components.Contains(artifact.Id)));
        foreach (var artifact in ImageGenerationCatalog.Manifest.Artifacts)
        foreach (var file in artifact.Files)
        {
            Assert.AreEqual(64, file.Sha256.Length); Assert.IsTrue(file.Sha256.All(Uri.IsHexDigit));
            Assert.IsTrue(file.SourceUrl.Contains(artifact.Revision, StringComparison.Ordinal));
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
                Assert.AreNotEqual("Generation.Model." + model.Id, l.T("Generation.Model." + model.Id));
        }
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
    }
    [TestMethod]
    public async Task CancellationRetainsFinishedImagesAndResumeDoesNotGenerateThemAgain()
    {
        var request = Request(); var worker = new Worker(request.Prompt) { CancelAt = 1 };
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
        await Assert.ThrowsAsync<InvalidDataException>(() => new ImageGenerationRuntime(worker).RunAsync(request, [], null, CancellationToken.None));
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
