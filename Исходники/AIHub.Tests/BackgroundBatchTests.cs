using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class BackgroundBatchTests
{
    [TestMethod]
    public async Task PauseAndRestartReusesFirstImageAndProducesOneCompleteOutput()
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-background-batch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var exit = new CancellationTokenSource();
        try
        {
            var source = Path.Combine(root, "source.bin"); File.WriteAllText(source, "fixture");
            var store = new ImageBatchStore(Path.Combine(root, "batches"));
            var job = new ImageBatchJob { Settings = new() { LanguageCode = "en" }, Items = Enumerable.Range(0, 2).Select(_ => new ImageBatchItem
                { File = new() { SourcePath = source, Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))) } }).ToList() };
            var operationStore = new BackgroundOperationStore(Path.Combine(root, "operation.json"));
            var controller = new BackgroundOperationController(operationStore); Host(controller, exit.Token);
            var model = new Fake { BlockSecond = true };
            var state = new BackgroundOperationState { Kind = "image.batch", Title = "Fixture", Input = JsonSerializer.SerializeToElement(job.Id) };
            var work = ApplicationBackgroundOperations.RunAsync("image.batch", "Fixture", job.Id, job.Id, async token =>
                { await new ImageBatchProcessor(store, model).RunAsync(job, null, token); return true; }, default, state);
            await model.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); await controller.PauseAsync();
            controller.CheckpointForExit(); exit.Cancel(); await Assert.ThrowsAsync<OperationCanceledException>(() => work);
            var second = new BackgroundOperationController(operationStore); second.Load(); Host(second);
            var resumed = store.LoadAll().Single(); var finish = new Fake();
            await ApplicationBackgroundOperations.RunAsync("image.batch", "Fixture", resumed.Id, resumed.Id, async token =>
                { await new ImageBatchProcessor(store, finish).RunAsync(resumed, null, token); return true; }, default, second.State);
            Assert.AreEqual(1, finish.Analyses); Assert.AreEqual("completed", resumed.Status);
            Assert.IsTrue(ImageBatchExporter.IsPresent(store.Results(resumed), resumed));
            Assert.AreEqual(controller.State!.Id, second.State!.Id);
        }
        finally
        {
            Host(null);
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            Directory.Delete(root, true);
        }
    }
    private static void Host(BackgroundOperationController? controller, CancellationToken token = default)
    {
        typeof(ApplicationBackgroundOperations).GetProperty(nameof(ApplicationBackgroundOperations.Current))!.SetValue(null, controller);
        typeof(ApplicationBackgroundOperations).GetProperty(nameof(ApplicationBackgroundOperations.ExitToken))!.SetValue(null, token);
    }
    private sealed class Fake : IImageBatchModel
    {
        public int Analyses; public bool BlockSecond;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Restart() { }
        public async Task<ImageBatchAnalysis> AnalyzeAsync(ImageAnalysisFilePassport file, ImageAnalysisLiterarySettings settings, CancellationToken token)
        {
            if (++Analyses == 2 && BlockSecond) { Started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            return new("The image shows a quiet house with windows and a road.", "A house");
        }
        public Task<IReadOnlyList<ImageBatchSection>> FormatAsync(IReadOnlyList<(string Id, ImageBatchAnalysis Analysis)> items,
            ImageAnalysisLiterarySettings settings, CancellationToken token)
            => Task.FromResult<IReadOnlyList<ImageBatchSection>>(items.Select(i => new ImageBatchSection(i.Id, "Description", [i.Analysis.Details])).ToArray());
    }
}
