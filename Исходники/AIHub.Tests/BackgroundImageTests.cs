using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class BackgroundImageTests
{
    private static void Host(BackgroundOperationController? controller, CancellationToken token = default)
    {
        typeof(ApplicationBackgroundOperations).GetProperty(nameof(ApplicationBackgroundOperations.Current))!.SetValue(null, controller);
        typeof(ApplicationBackgroundOperations).GetProperty(nameof(ApplicationBackgroundOperations.ExitToken))!.SetValue(null, token);
    }

    [TestMethod]
    public async Task PauseAndRestartReuseVisionAndPreserveTemporarySource()
    {
        using var f = new Fixture(); using var exit = new CancellationTokenSource();
        var first = new BackgroundOperationController(f.Background); Host(first, exit.Token);
        var pipeline = new Pipeline { Block = true };
        var work = new ImageBackgroundWork(f.Store).RunAsync(f.Session, f.Storage, "", () => pipeline, _ => { }, null, null, default);
        await pipeline.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await first.PauseAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("VISION", f.Store.Load(f.Session.SessionId, f.Storage)!.VisualReport);
        Assert.AreNotEqual(f.Source, f.Session.File!.SourcePath);
        File.Delete(f.Source); first.CheckpointForExit(); exit.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => work);
        var second = new BackgroundOperationController(f.Background); second.Load(); Host(second);
        var resumed = f.Store.Load(f.Session.SessionId, f.Storage)!;
        var finish = new Pipeline();
        await new ImageBackgroundWork(f.Store).RunAsync(resumed, f.Storage, "", () => finish, _ => { }, null, null, default, second.State);
        Assert.AreEqual(0, finish.VisionCalls); Assert.HasCount(1, resumed.Versions);
        Assert.AreEqual(first.State!.Id, second.State!.Id);
    }

    [TestMethod]
    public async Task CompletedTextReceiptPreventsDuplicateGenerationWhenSpeechPauses()
    {
        using var f = new Fixture(); var controller = new BackgroundOperationController(f.Background); Host(controller);
        var pipeline = new Pipeline(); var speechStarted = new TaskCompletionSource(); var speechCalls = 0;
        var work = new ImageBackgroundWork(f.Store).RunAsync(f.Session, f.Storage, "", () => pipeline, _ => { }, null, null, default,
            afterText: async token => { if (++speechCalls == 1) { speechStarted.SetResult(); await Task.Delay(Timeout.Infinite, token); } });
        await speechStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); await controller.PauseAsync();
        Assert.HasCount(1, f.Session.Versions); await controller.ResumeAsync(default); await work;
        Assert.AreEqual(1, pipeline.ComposeCalls); Assert.HasCount(1, f.Session.Versions); Assert.AreEqual(2, speechCalls);
    }

    [TestMethod]
    public async Task ChangedExternalImageDoesNotRunModel()
    {
        using var f = new Fixture(); Host(new(f.Background)); f.Session.File!.StorageKind = ImageAssetKinds.External;
        File.AppendAllText(f.Source, "changed"); var pipeline = new Pipeline();
        await Assert.ThrowsAsync<InvalidDataException>(() => new ImageBackgroundWork(f.Store).RunAsync(f.Session, f.Storage,
            "", () => pipeline, _ => { }, null, null, default));
        Assert.AreEqual(0, pipeline.ComposeCalls); Assert.HasCount(0, f.Session.Versions);
    }

    [TestMethod]
    public async Task RevisionUsesSavedSelectedVersionAndKeepsOneResult()
    {
        using var f = new Fixture(); Host(new(f.Background));
        f.Session.Versions.Add(new() { VersionId = "old", Text = "OLD", Number = 1 });
        f.Session.SelectedVersionId = "old"; var pipeline = new Pipeline();
        var input = new ImageBackgroundInput(f.Session.SessionId, f.Storage, "revise", "EDIT", "old", "fixed");
        var state = new BackgroundOperationState { Kind = ImageBackgroundWork.Kind, Title = "Image", Input = JsonSerializer.SerializeToElement(input) };
        var runner = new ImageBackgroundWork(f.Store);
        await runner.RunAsync(f.Session, f.Storage, "EDIT", () => pipeline, _ => { }, null, null, default, state);
        await runner.RunAsync(f.Session, f.Storage, "EDIT", () => pipeline, _ => { }, null, null, default, state);
        Assert.AreEqual(1, pipeline.RevisionCalls); Assert.HasCount(2, f.Session.Versions);
        Assert.AreEqual("OLD_EDIT", f.Session.GetSelectedVersion()!.Text);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "lopata-background-image-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(Root, "image.png");
        public StorageSettings Storage { get; } = new();
        public ImageAnalysisSessionStore Store { get; } = new();
        public BackgroundOperationStore Background => new(Path.Combine(Root, "background.json"));
        public ImageAnalysisLiterarySession Session { get; } = new();
        public Fixture()
        {
            Directory.CreateDirectory(Root); File.WriteAllText(Source, "IMAGE");
            Storage.Results.Locations.Add(new() { Path = Root });
            Session.File = new() { SourcePath = Source, Extension = ".png", StorageKind = ImageAssetKinds.Temporary,
                Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Source))), DisplayName = "image.png" };
        }
        public void Dispose()
        {
            Host(null);
            if (!Path.GetFullPath(Root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            Directory.Delete(Root, true);
        }
    }

    private sealed class Pipeline : ISingleImageLiteraryPipeline
    {
        public bool Block; public int VisionCalls, ComposeCalls, RevisionCalls;
        public TaskCompletionSource Started { get; } = new();
        public string PipelineId => ImageAnalysisPipelineIds.Legacy;
        public Task PrepareAsync(StorageSettings settings, ImageAnalysisLiterarySession? session, bool concurrently,
            Action<string> log, IProgress<ImageAnalysisLiteraryProgress>? progress, CancellationToken token) => Task.CompletedTask;
        public async Task<ImageAnalysisLiteraryResult> CreateAsync(ImageAnalysisFilePassport passport, ImageAnalysisLiterarySettings settings,
            StorageSettings storage, ImageAnalysisLiterarySession session, Action<string> log, IProgress<ImageAnalysisLiteraryProgress>? progress,
            IProgress<ModelStreamChunk>? stream, Action<ImageAnalysisPipelineCheckpoint>? checkpoint, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(session.VisualReport)) { VisionCalls++; checkpoint?.Invoke(new("VISION", [])); }
            ComposeCalls++; Started.TrySetResult(); if (Block) await Task.Delay(Timeout.Infinite, token);
            return new("VISION", "DESCRIPTION", new());
        }
        public Task<string> ReviseAsync(ImageAnalysisLiterarySession session, string request, StorageSettings storage,
            Action<string> log, IProgress<ImageAnalysisLiteraryProgress>? progress, IProgress<ModelStreamChunk>? stream, CancellationToken token)
        { RevisionCalls++; return Task.FromResult(session.GetSelectedVersion()!.Text + "_" + request); }
        public void Stop() { } public void Dispose() { }
    }
}
