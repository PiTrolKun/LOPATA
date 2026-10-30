using System.IO;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.VideoPreparation;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoShotsReviewPhysicalTests
{
    [TestMethod]
    public async Task InstalledSelectedAssistantReviewsKnownBadAndConsistentScene()
    {
        var path = Environment.GetEnvironmentVariable("LOPATA_SHOTS_REVIEW_RECEIPT");
        var output = Environment.GetEnvironmentVariable("LOPATA_SHOTS_REVIEW_OUTPUT");
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(output)) Assert.Inconclusive("Explicit installed model review required.");
        Directory.CreateDirectory(output);
        using var receipt = JsonDocument.Parse(File.ReadAllText(path));
        var original = receipt.RootElement.GetProperty("Request").Deserialize<VideoConceptRequest>()!;
        var model = Environment.GetEnvironmentVariable("LOPATA_SHOTS_REVIEW_MODEL");
        var modelPath = Environment.GetEnvironmentVariable("LOPATA_SHOTS_REVIEW_MODEL_PATH");
        if (!string.IsNullOrEmpty(model) && !string.IsNullOrEmpty(modelPath)) original = original with { ModelId = model, ModelPath = modelPath };
        var answer = receipt.RootElement.GetProperty("Answer").Deserialize<VideoConceptAnswer>()!;
        var scene = JsonSerializer.Deserialize<VideoScenePlan>(answer.Text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        scene.Id = JsonSerializer.Deserialize<VideoScenePlan>(original.Text)!.Id;
        var assistant = new VideoConceptAssistant(new(new UserProfileStore(), new IpLocationService()));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        try
        {
            var bad = original with { Id = Guid.NewGuid().ToString("N"), PromptVersion = VideoShotsProtocol.Version,
                Purpose = VideoShotsQuality.Purpose, Text = JsonSerializer.Serialize(scene), ContextOriginal = "" };
            var result = await assistant.GenerateShotsAsync(bad, null, deadline.Token);
            File.WriteAllText(Path.Combine(output, "bad-scene-review.json"), JsonSerializer.Serialize(result));
            Assert.IsNotEmpty(VideoShotsQuality.Read(result.Text));
            scene.Shots = scene.Shots.Take(1).ToList();
            var good = bad with { Id = Guid.NewGuid().ToString("N"), Text = JsonSerializer.Serialize(scene) };
            result = await assistant.GenerateShotsAsync(good, null, deadline.Token);
            File.WriteAllText(Path.Combine(output, "good-scene-review.json"), JsonSerializer.Serialize(result));
            Assert.IsEmpty(VideoShotsQuality.Read(result.Text));
            Console.WriteLine(JsonSerializer.Serialize(assistant.RuntimeInfo));
        }
        finally { await assistant.RetireAsync(); }
    }
}
