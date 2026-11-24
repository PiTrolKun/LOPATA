using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.AnimatedAssets;
using AIHub.Services.VisualScenario;
using SkiaSharp;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class AnimationPackRecoveryTests
{
    [TestMethod]
    public async Task RestoredPackSkipsFirstPoseAndCheckpointsOnlyAfterTheSecondIsSaved()
    {
        var id = Guid.NewGuid().ToString("N");
        var first = Guid.NewGuid().ToString("N");
        var second = Guid.NewGuid().ToString("N");
        var firstTarget = $"card/{Guid.NewGuid():N}/pose/{Guid.NewGuid():N}";
        var secondTarget = $"card/{Guid.NewGuid():N}/pose/{Guid.NewGuid():N}";
        var directory = Path.Combine(AppDataPaths.BaseDirectory, "AnimatedAssets", "ModelJobs", id);
        var root = Path.Combine(Path.GetTempPath(), "lopata-animation-pack-" + id);
        var store = new BackgroundOperationStore(Path.Combine(root, "operation.json"));
        var operation = new BackgroundOperationState
        {
            Kind = AnimationModelService.OperationKind,
            Title = "Pack test",
            Project = Path.Combine(root, "project.lopatazipP"),
            Phase = BackgroundOperationPhase.Waiting,
            Input = JsonSerializer.SerializeToElement(new
            {
                Id = id, Action = "draw-pack", Target = "pack", Prompt = "", Model = VisualComponents.Turbo,
                // An accidental fresh generation must fail before preparing or starting an artist.
                EngineSet = "recovery-fixture", Author = "Test", SubmittedAt = DateTimeOffset.UtcNow,
                HasImage = true, Transparent = false, Seed = 1, Duration = 0, Keep = "", Language = "English",
                Items = new[]
                {
                    new { Id = first, Target = firstTarget, Prompt = "First pose", Seed = 11 },
                    new { Id = second, Target = secondTarget, Prompt = "Second pose", Seed = 22 }
                }
            }),
            Checkpoint = JsonSerializer.SerializeToElement(new { Id = id, Target = "pack", Completed = 1, Total = 2, Finalized = false })
        };
        var request = new ImageGenerationRequest(second, VisualComponents.Turbo, "Second pose", 4, 4, [22], "", directory);
        var pngPath = ImageGenerationSessionStore.ResultPath(request, 0);
        var sourcePath = Path.Combine(directory, "source.png");
        var sessionPath = Path.Combine(directory, "session.json");
        var appliedPath = Path.Combine(root, "saved-pose.png");
        Directory.CreateDirectory(directory);
        using var bitmap = new SKBitmap(4, 4);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = data.ToArray();
        var previous = ApplicationBackgroundOperations.Current;
        var previousExit = ApplicationBackgroundOperations.ExitToken;
        try
        {
            File.WriteAllBytes(sourcePath, bytes);
            File.WriteAllBytes(pngPath, bytes);
            ImageGenerationSessionStore.Save(directory, new(id, [new(request, [new(0, Path.GetFileName(pngPath), 22)])]));
            store.Save(operation);
            var controller = new BackgroundOperationController(store);
            var service = new AnimationModelService(new StorageSettings(), parallel: 1, localize: key => key);
            var applied = new List<string>();
            controller.Register(AnimationModelService.OperationKind, (state, token) => service.RestoreAsync(state, result =>
            {
                var checkpoint = store.Operation(operation.Id).Load()!.Checkpoint!.Value;
                Assert.IsFalse(checkpoint.GetProperty("Finalized").GetBoolean());
                applied.Add(result.Target);
                if (result.Target == secondTarget)
                {
                    Assert.AreEqual(1, checkpoint.GetProperty("Completed").GetInt32());
                    File.WriteAllBytes(appliedPath, result.Drawing!.Image);
                }
                else
                {
                    Assert.AreEqual("pack", result.Target);
                    Assert.AreEqual(2, checkpoint.GetProperty("Completed").GetInt32());
                    Assert.IsTrue(File.Exists(appliedPath));
                }
                return Task.CompletedTask;
            }, token));
            ApplicationBackgroundOperations.Current = controller;
            ApplicationBackgroundOperations.ExitToken = default;
            controller.Load();
            await controller.ResumeAsync(default);

            CollectionAssert.AreEqual(new[] { secondTarget, "pack" }, applied);
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(appliedPath));
            Assert.AreEqual(BackgroundOperationPhase.Completed, controller.State!.Phase);
            Assert.IsTrue(controller.State.Checkpoint!.Value.GetProperty("Finalized").GetBoolean());
            Assert.IsFalse(Directory.Exists(directory));
        }
        finally
        {
            ApplicationBackgroundOperations.Current = previous;
            ApplicationBackgroundOperations.ExitToken = previousExit;
            if (Directory.Exists(directory))
            {
                foreach (var file in new[] { pngPath, sourcePath, sessionPath })
                    File.Delete(file);
                Directory.Delete(directory);
            }
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
