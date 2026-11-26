using System.IO;
using System.Text;
using AIHub.Services;
using AIHub.Services.SongClip;

namespace AIHub.Tests;

[TestClass]
public sealed class SongClipPlannerAcquisitionTests
{
    [TestMethod]
    [DataRow("glitchframe", "core", ManagedModelCatalog.CoreArtifactId, CoreModelManager.CoreModelFileName, "qwen3")]
    [DataRow(H3MusicRunner.ModelId, "executor", ManagedModelCatalog.Qwen36ExecutorArtifactId, ManagedModelCatalog.Qwen36ExecutorFile, "qwen35")]
    public async Task MissingPlannerAcquiresThePinnedCardAndUsesTheReturnedFile(string generator, string planner,
        string artifactId, string fileName, string architecture)
    {
        using var files = new VideoPreparationTests.TestFiles();
        using var cancellation = new CancellationTokenSource();
        var attempt = Attempt(files, generator);
        var path = Path.Combine(attempt.ModelsRoot, "acquired", fileName);
        var calls = 0;

        var request = await SongClipPlannerModel.ResolveForPlanningAsync(attempt, (card, token) =>
        {
            calls++;
            Assert.AreEqual(cancellation.Token, token);
            Assert.AreEqual(artifactId, card.ModelArtifactId);
            Assert.AreEqual(architecture, card.Architecture);
            var weight = card.Files.Single(file => file.IsRequired);
            Assert.AreEqual(fileName, weight.RelativePath);
            Assert.AreEqual(64, weight.Sha256.Length);
            WriteModel(path, architecture);
            return Task.FromResult(path);
        }, cancellation.Token);

        Assert.AreEqual(1, calls);
        Assert.AreEqual(planner, request.ModelId);
        Assert.AreEqual(path, request.ModelPath);
    }

    [TestMethod]
    public async Task InstalledPlannerIsReusedEvenWhenAnotherPlannerIsAlsoInstalled()
    {
        using var files = new VideoPreparationTests.TestFiles();
        var attempt = Attempt(files, H3MusicRunner.ModelId);
        var executor = Path.Combine(attempt.ModelsRoot, "existing", ManagedModelCatalog.Qwen36ExecutorFile);
        WriteModel(executor, "qwen35");
        WriteModel(Path.Combine(attempt.ModelsRoot, CoreModelManager.CoreModelFileName), "qwen3");

        var request = await SongClipPlannerModel.ResolveForPlanningAsync(attempt,
            (_, _) => throw new AssertFailedException("An installed planner must not be downloaded again."), default);

        Assert.AreEqual("executor", request.ModelId);
        Assert.AreEqual(executor, request.ModelPath);
    }

    [TestMethod]
    public async Task CanceledAcquisitionReturnsNoPlannerAndRetryUsesTheSameArtifact()
    {
        using var files = new VideoPreparationTests.TestFiles();
        using var cancellation = new CancellationTokenSource();
        var attempt = Attempt(files, H3MusicRunner.ModelId);
        var path = Path.Combine(attempt.ModelsRoot, ManagedModelCatalog.Qwen36ExecutorFile);
        var requested = new List<string>();

        await Assert.ThrowsAsync<OperationCanceledException>(() => SongClipPlannerModel.ResolveForPlanningAsync(attempt,
            (card, _) =>
            {
                requested.Add(card.ModelArtifactId);
                cancellation.Cancel();
                return Task.FromResult(path);
            }, cancellation.Token));
        Assert.IsFalse(File.Exists(path));

        var request = await SongClipPlannerModel.ResolveForPlanningAsync(attempt, (card, _) =>
        {
            requested.Add(card.ModelArtifactId);
            WriteModel(path, "qwen35");
            return Task.FromResult(path);
        }, default);

        CollectionAssert.AreEqual(new[] { ManagedModelCatalog.Qwen36ExecutorArtifactId, ManagedModelCatalog.Qwen36ExecutorArtifactId },
            requested.ToArray());
        Assert.AreEqual(path, request.ModelPath);
    }

    [TestMethod]
    public async Task ExplicitPlannerOverrideDoesNotAcquireTheDefaultPlanner()
    {
        using var files = new VideoPreparationTests.TestFiles();
        var attempt = Attempt(files, "glitchframe");
        var error = await Assert.ThrowsExactlyAsync<FileNotFoundException>(() =>
            SongClipPlannerModel.ResolveForPlanningAsync(attempt,
                (_, _) => throw new AssertFailedException("An explicit override must not trigger acquisition."), default,
                plannerId: "executor"));
        Assert.AreEqual("SongClip.PlannerModelMissing", error.Message);
    }

    private static SongClipAttempt Attempt(VideoPreparationTests.TestFiles files, string generator) => new(
        Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "song.wav", "", "", "", "", new(), files.File("models"), 4)
        { Model = generator, PlanningVersion = 1 };

    private static void WriteModel(string path, string architecture)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.UTF8);
        writer.Write(0x46554747u);
        writer.Write(3u);
        writer.Write(0ul);
        writer.Write(3ul);
        WriteString(writer, "general.architecture");
        writer.Write(8u);
        WriteString(writer, architecture);
        WriteNumber(writer, architecture + ".block_count", 32u);
        WriteNumber(writer, architecture + ".context_length", 32768u);
    }

    private static void WriteNumber(BinaryWriter writer, string key, uint value)
    {
        WriteString(writer, key);
        writer.Write(4u);
        writer.Write(value);
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write((ulong)bytes.Length);
        writer.Write(bytes);
    }
}
