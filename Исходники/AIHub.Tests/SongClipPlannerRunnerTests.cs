using System.IO;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.SongClip;

namespace AIHub.Tests;

[TestClass]
public sealed class SongClipPlannerRunnerTests
{
    private static readonly SongClipSettings Settings = new() { Width = 640, Height = 360, Fps = 24 };
    private static readonly OutputModelUse Planner = new("tested-qwen", "Tested Qwen", "tested-revision", "song-clip-visual-planning");
    private static OutputModelUse[] BaseModels => [
        new("stabilityai/stable-diffusion-xl-base-1.0", "SDXL 1.0 FP16", "462165984030d82259a11f4367a4eed129e94a7b", "background-image-generation"),
        new("OlaProeis/Glitchframe", "Glitchframe", "a9660508fdbbfbc6243f570d71bbd2af8ee5b111", "music-video-composition")
    ];

    [TestMethod]
    public void PlannedGlitchResultRequiresActualPlannerRevisionAndRoleWhileLegacyHistoryRemainsValid()
    {
        Assert.AreEqual(8d, GlitchframeRunner.ValidateResult(Output(BaseModels), Source(), Settings));
        Assert.Throws<InvalidDataException>(() => GlitchframeRunner.ValidateResult(Output(BaseModels), Source(), Settings, [Planner]));
        Assert.Throws<InvalidDataException>(() => GlitchframeRunner.ValidateResult(Output([..BaseModels, Planner with { Revision = "wrong" }]), Source(), Settings, [Planner]));
        Assert.Throws<InvalidDataException>(() => GlitchframeRunner.ValidateResult(Output([..BaseModels, Planner with { Role = "unrelated chat" }]), Source(), Settings, [Planner]));
        Assert.AreEqual(8d, GlitchframeRunner.ValidateResult(Output([..BaseModels, Planner]), Source(), Settings, [Planner]));
    }

    [TestMethod]
    public void EveryRecordedPlanningAssistantMustBePresentInFinalResult()
    {
        var second = Planner with { Id = "tested-larger-qwen", Name = "Tested larger Qwen", Revision = "other-pinned-revision" };
        Assert.Throws<InvalidDataException>(() => GlitchframeRunner.ValidateResult(Output([..BaseModels, Planner]), Source(), Settings, [Planner, second]));
        Assert.AreEqual(8d, GlitchframeRunner.ValidateResult(Output([..BaseModels, Planner, second]), Source(), Settings, [Planner, second]));
    }

    [TestMethod]
    public void EmptyH3CreativeDirectionIsAllowedOnlyForNewSemanticPlanning()
    {
        var attempt = new SongClipAttempt(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "attachments/song.wav", "", "Test",
            "Кот чинит часы", "", Settings with { Prompt = "" }, "unused-model-storage", 2) { Model = H3MusicRunner.ModelId };
        Assert.Throws<InvalidDataException>(() => H3MusicRunner.ValidateSettings(attempt));
        H3MusicRunner.ValidateSettings(attempt with { PlanningVersion = 1 });
        Assert.Throws<InvalidDataException>(() => H3MusicRunner.ValidateSettings(attempt with { PlanningVersion = 2 }));
    }

    private static JsonElement Source() => JsonSerializer.SerializeToElement(new {
        streams = new[] { new { codec_type = "audio", duration = "8.0" } }, format = new { duration = "8.0" }
    });
    private static JsonElement Output(OutputModelUse[] models)
    {
        var provenance = new OutputProvenance { Scenario = "song-to-clip/glitchframe", GenerationSeconds = 42, ModelHistory = "recorded-inference", Models = models };
        return JsonSerializer.SerializeToElement(new {
            streams = new object[] { new { codec_type = "video", width = Settings.Width, height = Settings.Height, duration = "8.0" },
                new { codec_type = "audio", duration = "8.0" } },
            format = new { duration = "8.0", tags = new Dictionary<string, string> { ["LOPATA_VIDEO"] = provenance.Json() } }
        });
    }
}
