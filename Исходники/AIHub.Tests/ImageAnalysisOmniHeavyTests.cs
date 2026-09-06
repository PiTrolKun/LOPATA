using System.IO;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class ImageAnalysisOmniHeavyTests
{
    [TestMethod]
    public void RuntimeProtocol_UsesUtf8WithoutByteOrderMark()
    {
        var encoding = Qwen25OmniRuntimeService.ProtocolEncoding;
        var payload = encoding.GetBytes("{\"id\":1}");

        Assert.HasCount(0, encoding.GetPreamble());
        Assert.AreEqual((byte)'{', payload[0]);
    }

    [TestMethod]
    public void Catalog_HeavyUsesOneAvailableOmniComponent()
    {
        var heavy = ImageAnalysisBundleCatalog.Create().Single(bundle =>
            bundle.Id == ImageAnalysisBundleCatalog.HeavyId);

        Assert.IsTrue(heavy.IsAvailable);
        Assert.AreEqual(1, heavy.Components.Count);
        Assert.AreEqual(ManagedModelCatalog.OmniGammaDisplayName, heavy.Components[0].ModelName);
        Assert.AreEqual("ImageAnalysis.Placement.Gpu", heavy.Components[0].PlacementKey);
        Assert.AreEqual(20, heavy.Requirements.FreeDiskGb);
    }

    [TestMethod]
    public void ManagedCatalog_OmniManifestIsCompleteAndPinned()
    {
        var card = ManagedModelCatalog.CreateQwen25OmniHeavy(@"C:\models");

        Assert.AreEqual(ManagedModelCatalog.Qwen25OmniRepository, card.RepositoryId);
        Assert.AreEqual(ManagedModelCatalog.Qwen25OmniRevision, card.Revision);
        StringAssert.Contains(card.License, "Qwen Research License");
        Assert.AreEqual(16, card.Files.Count);
        Assert.AreEqual(3, card.Files.Count(file => file.RelativePath.EndsWith(".safetensors", StringComparison.Ordinal)));
        Assert.AreEqual(11_989_065_629, card.Files.Sum(file => file.SizeBytes));
        Assert.IsTrue(card.Files.Any(file => file.RelativePath == "LICENSE"));
        Assert.IsTrue(card.Files.All(file => file.IsRequired));
        Assert.IsTrue(card.Files.All(file => file.Sha256.Length == 64));
        Assert.IsTrue(card.Files.All(file => file.SourceUrl.Contains(ManagedModelCatalog.Qwen25OmniRevision, StringComparison.Ordinal)));
    }

    [TestMethod]
    public void PromptBuilder_RussianObservationRequestsVisibleFactsWithoutExhaustiveLoop()
    {
        var prompt = ImageAnalysisOmniPromptBuilder.BuildObservationPrompt(new ImageAnalysisLiterarySettings
        {
            LanguageCode = "ru",
            Wishes = "проверь надписи"
        });

        StringAssert.Contains(prompt, "Опиши, что видишь на изображении");
        Assert.IsFalse(prompt.Contains("максимум", StringComparison.Ordinal));
        StringAssert.Contains(prompt, "проверь надписи");
        Assert.IsFalse(prompt.Contains("{{", StringComparison.Ordinal));
        Assert.IsFalse(prompt.Contains("token", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void PromptBuilder_EnglishComposeCarriesSettingsAndStrictJsonContract()
    {
        var prompt = ImageAnalysisOmniPromptBuilder.BuildComposePrompt(new ImageAnalysisLiterarySettings
        {
            LanguageCode = "en-US",
            Accuracy = ImageAnalysisAccuracyModes.Strict,
            Style = ImageAnalysisLiteraryStyles.Dramatic,
            Length = ImageAnalysisTextLengths.Detailed,
            Form = ImageAnalysisTextForms.Continuous,
            Wishes = "keep the mood restrained"
        });

        StringAssert.Contains(prompt, "language: English");
        StringAssert.Contains(prompt, "strict");
        StringAssert.Contains(prompt, "dramatic");
        StringAssert.Contains(prompt, "detailed");
        StringAssert.Contains(prompt, "continuous prose");
        StringAssert.Contains(prompt, "keep the mood restrained");
        StringAssert.Contains(prompt, "exactly one JSON object");
        Assert.IsFalse(prompt.Contains("{{", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ResultParser_AcceptsOnlyCompleteStrictContractAndPreservesConversation()
    {
        var conversation = new List<ImageAnalysisHiddenMessage>
        {
            new() { Role = "user", Content = "observe", IncludesImage = true },
            new() { Role = "assistant", Content = "visual report" },
            new() { Role = "user", Content = "compose" },
            new() { Role = "assistant", Content = "json" }
        };
        const string json = """
            {"title":"Title","paragraphs":["First.","Second."],"review_items":["one","two","three"],"uncertainties":["maybe"]}
            """;

        var result = ImageAnalysisOmniResultParser.Parse("visual report", json, conversation, 12, 34);

        StringAssert.Contains(result.Description, "Title");
        Assert.AreEqual(3, result.ReviewSummary.Items.Count);
        Assert.AreEqual(4, result.HiddenConversation?.Count);
        Assert.AreEqual(12, result.VisualPassMilliseconds);
        Assert.AreEqual(34, result.ComposePassMilliseconds);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(6)]
    [DataRow(7)]
    [DataRow(20)]
    public void ResultParser_PreservesReviewItemsWithoutCountLimits(int count)
    {
        var items = Enumerable.Range(1, count).Select(index => $"Detail {index}").ToArray();
        var response = System.Text.Json.JsonSerializer.Serialize(new
        {
            title = "Title",
            paragraphs = new[] { "Complete description." },
            review_items = items,
            uncertainties = Array.Empty<string>()
        });

        var result = ImageAnalysisOmniResultParser.Parse("visual report", response, [], 12, 34);

        Assert.AreEqual("Title" + Environment.NewLine + Environment.NewLine + "Complete description.", result.Description);
        CollectionAssert.AreEqual(items, result.ReviewSummary.Items.ToArray());
        Assert.AreEqual(response, result.RawFinalResponse);
    }

    [TestMethod]
    public void ResultParser_AcceptsOneWrappedObjectButRejectsUnknownFields()
    {
        const string response = """
            {"title":null,"paragraphs":["Text"],"review_items":["one","two","three"],"uncertainties":[],"extra":"not allowed"}
            """;

        Assert.ThrowsExactly<InvalidDataException>(() =>
            ImageAnalysisOmniResultParser.Parse("visual", response, [], 0, 0));
        var accepted = ImageAnalysisOmniResultParser.Parse(
            "visual",
            "Результат:\n```json\n{\"title\":null,\"paragraphs\":[\"Text\"],\"review_items\":[\"one\",\"two\",\"three\"],\"uncertainties\":[]}\n```",
            [],
            0,
            0);
        Assert.AreEqual("Text", accepted.Description);
    }

    [TestMethod]
    public void ResourcePlanner_UsesWorstSampleAndKeepsSafetyReserves()
    {
        const long gib = 1024L * 1024 * 1024;
        var planner = new ImageAnalysisHeavyResourcePlanningService();
        var plan = planner.Calculate(
        [
            new(DateTimeOffset.Now, 100 * gib, 128 * gib, 140 * gib, 160 * gib, 22 * gib, 24 * gib),
            new(DateTimeOffset.Now, 96 * gib, 128 * gib, 130 * gib, 160 * gib, 20 * gib, 24 * gib)
        ]);

        Assert.AreEqual(96 * gib, plan.AvailableRamBytes);
        Assert.AreEqual(20 * gib, plan.AvailableVramBytes);
        Assert.AreEqual(96 * gib - (long)(128 * gib * 0.10), plan.CpuBudgetBytes);
        Assert.AreEqual(18 * gib, plan.GpuBudgetBytes);
        Assert.IsTrue(plan.HasEnoughGpuMemory);
        Assert.AreEqual("gpu_only_required", plan.Strategy);
    }

    [TestMethod]
    public void ResourceMonitor_WarnsAfterMaterialPostWarmupDropWithoutChangingPlan()
    {
        const long gib = 1024L * 1024 * 1024;
        var planner = new ImageAnalysisHeavyResourcePlanningService();
        var plan = planner.Calculate(
        [
            new(DateTimeOffset.Now, 100 * gib, 128 * gib, 140 * gib, 160 * gib, 22 * gib, 24 * gib)
        ]);
        var baseline = new ImageAnalysisHeavyResourceSample(
            DateTimeOffset.Now, 14 * gib, 128 * gib, 18 * gib, 160 * gib, 6 * gib, 24 * gib);
        var current = new ImageAnalysisHeavyResourceSample(
            DateTimeOffset.Now, 5 * gib, 128 * gib, 5 * gib, 160 * gib, 1 * gib, 24 * gib);

        var status = planner.EvaluatePostWarmupPressure(plan, baseline, current);

        Assert.IsTrue(status.RestartRecommended);
        Assert.IsTrue(status.RamPressure);
        Assert.IsTrue(status.CommitPressure);
        Assert.IsTrue(status.VramPressure);
        Assert.AreEqual(22 * gib - (long)(22 * gib * 0.10), plan.GpuBudgetBytes);
    }

    [TestMethod]
    public void ResourcePlanner_RejectsHeavyWhenFullGpuPlacementCannotBeGuaranteed()
    {
        const long gib = 1024L * 1024 * 1024;
        var planner = new ImageAnalysisHeavyResourcePlanningService();
        var plan = planner.Calculate(
        [
            new(DateTimeOffset.Now, 96 * gib, 128 * gib, 130 * gib, 160 * gib, 15 * gib, 24 * gib)
        ]);

        Assert.IsFalse(plan.HasEnoughGpuMemory);
        Assert.AreEqual("gpu_only_unavailable", plan.Strategy);
        Assert.IsGreaterThan(0, plan.CpuBudgetBytes);
    }

    [TestMethod]
    public void HeavySpeech_DefaultsToKokoroAndKeepsRetiredOmniSettings()
    {
        var settings = new ImageAnalysisHeavySpeechSettings();

        Assert.AreEqual(ImageAnalysisSpeechModes.Kokoro, settings.Mode);
        Assert.AreEqual(ImageAnalysisOmniSpeakers.Ethan, settings.OmniSpeaker);
        settings.OmniVolume = 75;
        settings.KokoroVolume = 45;
        settings.ProgrammaticVolume = 20;
        settings.Mode = ImageAnalysisSpeechModes.Kokoro;
        Assert.AreEqual(45, settings.GetActiveVolume());
        settings.Mode = ImageAnalysisSpeechModes.Programmatic;
        Assert.AreEqual(20, settings.GetActiveVolume());
        settings.Mode = ImageAnalysisSpeechModes.Omni;
        Assert.AreEqual(ImageAnalysisSpeechModes.Kokoro, settings.Mode);
        Assert.AreEqual(45, settings.GetActiveVolume());
        Assert.AreEqual(75, settings.OmniVolume);
    }

    [TestMethod]
    public void HeavySpeech_MigratesPersistedOmniButPreservesExplicitOffAndPc()
    {
        var restored = System.Text.Json.JsonSerializer.Deserialize<ImageAnalysisHeavySpeechSettings>(
            """{"Mode":"omni","OmniSpeaker":"Chelsie","OmniVolume":75,"KokoroVolume":42}""");
        Assert.IsNotNull(restored);
        Assert.AreEqual(ImageAnalysisSpeechModes.Kokoro, restored.Mode);
        Assert.AreEqual(42, restored.GetActiveVolume());
        Assert.AreEqual("Chelsie", restored.OmniSpeaker);
        Assert.AreEqual(75, restored.OmniVolume);
        restored.Mode = ImageAnalysisSpeechModes.Off;
        Assert.AreEqual(ImageAnalysisSpeechModes.Off, restored.Mode);
        restored.Mode = ImageAnalysisSpeechModes.Programmatic;
        Assert.AreEqual(ImageAnalysisSpeechModes.Programmatic, restored.Mode);
        Assert.AreEqual(ImageAnalysisSpeechModes.Kokoro, ImageAnalysisSpeechModes.NormalizeHeavy(null));
        Assert.AreEqual(ImageAnalysisSpeechModes.Off, new ImageAnalysisSpeechSettings().Mode);
    }

    [TestMethod]
    public void HeavyPipeline_DoesNotExposeRetiredOmniSpeech()
    {
        Assert.IsFalse(typeof(IOmniSpeechPipeline).IsAssignableFrom(typeof(OmniHeavySingleImageLiteraryPipeline)));
    }

    [TestMethod]
    public void HeavySpeech_OnlyAcceptsOfficialQwen25Speakers()
    {
        Assert.AreEqual(ImageAnalysisOmniSpeakers.Ethan, ImageAnalysisOmniSpeakers.Normalize("Aiden"));
        Assert.AreEqual(ImageAnalysisOmniSpeakers.Chelsie, ImageAnalysisOmniSpeakers.Normalize("Chelsie"));
    }
}
