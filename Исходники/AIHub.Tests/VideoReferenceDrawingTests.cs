using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VideoPreparation;
using SkiaSharp;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoReferenceDrawingTests
{
    internal static (VideoPreparationProject Project, VideoReferencePrompt Item) Root()
    {
        var p = VideoReferencePlanTests.Root();
        VideoReferencePlans.Apply(p, VideoReferencePlanTests.Request(p), VideoReferencePlanTests.ListAnswer(p));
        var item = p.References!.Plan!.Items.Single();
        VideoReferencePlans.Apply(p, VideoReferencePlanTests.Request(p, item), VideoReferencePlanTests.PromptAnswer());
        p.References.Plan.Page = "ready"; return (p, item);
    }
    internal static VideoReferenceImageRequest Request(VideoPreparationProject p, VideoReferencePrompt item, string root) =>
        new(p.Id, Path.Combine(root, "project.lopataAIVid"), item.Id, VideoReferencePlans.Definition(item), item.CardId, item.Purpose,
            VideoReferences.Description(p, item.CardId)!, new(Guid.NewGuid().ToString("N"), "z-image", item.English, 256, 256,
                [42], root, ImageGenerationSessionStore.Create(root)) { Metadata = new("Test author", VideoReferenceImageWork.Kind), SubmittedAt = DateTimeOffset.UtcNow });
    [TestMethod]
    public async Task VariantsArePortableByteExactExplicitlySelectedAndReplayDoesNotDuplicate()
    {
        using var f = new VideoPreparationTests.TestFiles(); var (p, item) = Root(); var r = Request(p, item, f.File("drawing"));
        var worker = new Worker(); var runtime = new ImageGenerationRuntime(worker); var turn = await runtime.RunAsync(r.Image, [], null, default);
        var source = ImageGenerationSessionStore.ResultPath(r.Image, 0); var bytes = File.ReadAllBytes(source);
        var offset = 8; string? metadata = null;
        while (offset < bytes.Length)
        {
            var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, 4));
            if (System.Text.Encoding.ASCII.GetString(bytes, offset + 4, 4) == "iTXt")
            {
                var fields = System.Text.Encoding.UTF8.GetString(bytes, offset + 8, length).Split('\0', 6);
                if (fields[0] == "LOPATA") metadata = fields[5];
            }
            offset += length + 12;
        }
        using (var json = JsonDocument.Parse(metadata!))
        { Assert.AreEqual(VideoReferenceImageWork.Kind, json.RootElement.GetProperty("Scenario").GetString());
            Assert.AreEqual(r.Image.Id, json.RootElement.GetProperty("OperationId").GetString()); }
        await VideoReferenceImageWork.ApplyAsync(p, r, turn, f.File("copies"), default);
        await VideoReferenceImageWork.ApplyAsync(p, r, turn, f.File("copies"), default);
        Assert.HasCount(1, p.References!.Drawing!.Variants); Assert.HasCount(1, p.References.Images);
        var variant = p.References.Drawing.Variants.Single(); Assert.IsFalse(VideoReferenceDrawings.Ready(p));
        VideoReferenceDrawings.Select(p, variant, true); Assert.IsTrue(VideoReferenceDrawings.Ready(p));
        Assert.IsTrue(VideoReferencePlans.Ready(p), "Selecting an output must not invalidate the input prompt set.");
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(VideoReferences.PathFor(p, p.References.Images.Single())));
        VideoPreparationArchive.Save(p, r.ProjectPath);
        var opened = VideoPreparationArchive.Load(r.ProjectPath, f.File("extracted"));
        Assert.IsTrue(VideoReferenceDrawings.Ready(opened)); Assert.IsTrue(opened.Provenance.Models.Any(m => m.Role == "reference-image"));
        CollectionAssert.AreEqual(SHA256.HashData(bytes), SHA256.HashData(File.ReadAllBytes(VideoReferences.PathFor(opened, opened.References!.Images.Single()))));
        VideoReferenceDrawings.Select(opened, opened.References.Drawing!.Variants.Single(), false); Assert.IsFalse(VideoReferenceDrawings.Ready(opened));
        var replay = await runtime.RunAsync(JsonSerializer.Deserialize<ImageGenerationRequest>(JsonSerializer.Serialize(r.Image))!, [], null, default);
        Assert.AreEqual(1, worker.Calls); Assert.HasCount(1, replay.Results);
    }
    [TestMethod]
    public async Task WorldEditsAndPromptEditsNeverSilentlyConfirmAnOldVariant()
    {
        using var f = new VideoPreparationTests.TestFiles(); var (p, item) = Root(); var r = Request(p, item, f.File("drawing"));
        var turn = await new ImageGenerationRuntime(new Worker()).RunAsync(r.Image, [], null, default);
        item.English = "User changed the prompt while drawing";
        await VideoReferenceImageWork.ApplyAsync(p, r, turn, f.File("copies"), default);
        var v = p.References!.Drawing!.Variants.Single(); Assert.AreEqual(r.Image.Prompt, v.Prompt);
        Assert.IsFalse(VideoReferenceDrawings.IsCurrent(p, v));
        Assert.ThrowsExactly<InvalidDataException>(() => VideoReferenceDrawings.Select(p, v, true));
        item.English = r.Image.Prompt; VideoReferenceDrawings.Select(p, v, true);
        p.World!.Cards[0].Fixed += " User changed costume.";
        Assert.IsFalse(VideoReferenceDrawings.Ready(p)); Assert.IsTrue(VideoReferences.NeedsReview(p, p.References.Bindings.Single()));
        VideoReferenceDrawings.Select(p, v, false); Assert.HasCount(0, p.References.Bindings);
        p.Validate();
    }
    [TestMethod]
    public async Task NewPromptSelectionCannotRetargetAnAlreadySubmittedResult()
    {
        using var f = new VideoPreparationTests.TestFiles(); var (p, item) = Root(); var r = Request(p, item, f.File("drawing"));
        var other = VideoReferencePlans.Add(p); other.Name = "Other"; p.References!.Plan!.SelectedId = other.Id;
        var turn = await new ImageGenerationRuntime(new Worker()).RunAsync(r.Image, [], null, default);
        await VideoReferenceImageWork.ApplyAsync(p, r, turn, f.File("copies"), default);
        Assert.AreEqual(item.Id, p.References.Drawing!.Variants.Single().PromptId);
        Assert.AreNotEqual(other.Id, p.References.Drawing.Variants.Single().PromptId);
        var foreign = p.Clone(); foreign.Id = Guid.NewGuid().ToString("N");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => VideoReferenceImageWork.ApplyAsync(foreign, r, turn, f.File("foreign"), default));
    }
    [TestMethod]
    public async Task BackgroundWorkUsesSharedPreparationAndRetainsNativeResultAcrossCommitRetry()
    {
        using var f = new VideoPreparationTests.TestFiles(); var (p, item) = Root(); var r = Request(p, item, f.File("drawing"));
        using var preparation = new Preparation(); var worker = new Worker(); var runtime = new ImageGenerationRuntime(worker);
        await Assert.ThrowsExactlyAsync<IOException>(() => VideoReferenceImageWork.RunAsync(r, "test", preparation, runtime,
            (_, _) => throw new IOException("save failed"), default));
        await VideoReferenceImageWork.RunAsync(r, "test", preparation, runtime,
            (request, turn) => VideoReferenceImageWork.ApplyAsync(p, request, turn, f.File("copies"), default), default);
        Assert.AreEqual(1, worker.Calls); Assert.AreEqual(2, preparation.Calls); Assert.IsFalse(preparation.Download);
        Assert.HasCount(1, p.References!.Drawing!.Variants);
    }
    internal sealed class Worker : IImageGenerationWorker
    {
        public int Calls;
        public Task GenerateAsync(ImageGenerationRequest r, int index, IReadOnlyList<ManagedModelArtifactCard> cards, string input, string output, CancellationToken token)
        {
            Calls++; token.ThrowIfCancellationRequested();
            using var bitmap = new SKBitmap(r.Width, r.Height); bitmap.Erase(SKColors.Coral);
            using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var file = File.Create(output); data.SaveTo(file); return Task.CompletedTask;
        }
    }
    internal sealed class Preparation : IImageGenerationPreparation
    {
        public int Calls; public bool Download;
        public int MaximumParallelConnections { get; set; }
        public bool IsReady(string root, string modelId) => true;
        public IReadOnlyList<ManagedModelArtifactCard> Register(string root, string modelId) => [];
        public Task<IReadOnlyList<ManagedModelArtifactCard>> CheckAsync(string root, string id, IProgress<ManagedModelDownloadProgress>? progress, CancellationToken token) => Task.FromResult<IReadOnlyList<ManagedModelArtifactCard>>([]);
        public Task<IReadOnlyList<ManagedModelArtifactCard>> PrepareAsync(string root, string id, bool download, IProgress<ManagedModelDownloadProgress>? progress, CancellationToken token)
        { Calls++; Download = download; return Task.FromResult<IReadOnlyList<ManagedModelArtifactCard>>([]); }
        public void Dispose() { }
    }
}
