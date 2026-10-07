using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class ImageAnalysisLiteraryScenarioTests
{
    [TestMethod]
    public void VisionPrompt_IsAPlainUnrestrictedRequest()
    {
        var prompt = ImageAnalysisLiteraryPromptBuilder.BuildVisionPrompt(
            new ImageAnalysisLiterarySettings());

        Assert.AreEqual("Describe what you see in this image.", prompt);
        Assert.IsFalse(prompt.Any(character => character is >= '\u0400' and <= '\u04FF'));
    }

    [TestMethod]
    public void KimiRuntime_UsesVerifiedChatLlmCpuProfile()
    {
        var arguments = ImageAnalysisKimiRequestBuilder.BuildArguments(
            @"C:\models\kimi.bin",
            54321,
            logicalProcessorCount: 32).ToList();

        AssertArgumentValue(arguments, "--host", "127.0.0.1");
        AssertArgumentValue(arguments, "--port", "54321");
        AssertArgumentValue(arguments, "---chat", @"C:\models\kimi.bin");
        AssertArgumentValue(arguments, "-n", "24");
        AssertArgumentValue(arguments, "--batch_size", "512");
        AssertArgumentValue(arguments, "-c", "4096");
        AssertArgumentValue(arguments, "--max_proj_length", "1024");
        Assert.Contains("+single_turn", arguments);
        AssertArgumentValue(arguments, "-ngl", "0");
    }

    [TestMethod]
    public void KimiRequest_HasOnlyTheImageAndPlainUserPrompt()
    {
        const string dataUri = "data:image/jpeg;base64,AQID";
        var json = ImageAnalysisKimiRequestBuilder.BuildRequestBody(
            dataUri,
            "Describe what you see in this image.");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var messages = root.GetProperty("messages");

        Assert.AreEqual(1, messages.GetArrayLength());
        Assert.AreEqual("user", messages[0].GetProperty("role").GetString());
        Assert.AreEqual(
            dataUri,
            messages[0].GetProperty("content")[0].GetProperty("image_url").GetProperty("url").GetString());
        Assert.AreEqual(
            "Describe what you see in this image.",
            messages[0].GetProperty("content")[1].GetProperty("text").GetString());
        Assert.IsFalse(root.TryGetProperty("temperature", out _));
        Assert.IsFalse(root.TryGetProperty("max_tokens", out _));
    }

    [TestMethod]
    public void KimiResponse_UsesChatLlmDeltaAndRemovesInternalReasoning()
    {
        var result = ImageAnalysisKimiRequestBuilder.ParseResponseContent("""
            {
              "choices": [
                {
                  "delta": {
                    "content": "◁think▷internal notes◁/think▷A person in a leather jacket."
                  }
                }
              ]
            }
            """);

        Assert.AreEqual("A person in a leather jacket.", result);
    }

    [TestMethod]
    public void KimiResponse_DoesNotLeakAnUnfinishedThinkingBlock()
    {
        Assert.ThrowsExactly<InvalidDataException>(() =>
            ImageAnalysisKimiRequestBuilder.ParseResponseContent("""
                {
                  "choices": [
                    {
                      "delta": {
                        "content": "<think>unfinished internal notes"
                      }
                    }
                  ]
                }
                """));
    }

    [TestMethod]
    public void InitialPrompt_ContainsIndependentSettingsAndVisualEvidence()
    {
        var settings = new ImageAnalysisLiterarySettings
        {
            LanguageCode = "en",
            Accuracy = ImageAnalysisAccuracyModes.Strict,
            Style = ImageAnalysisLiteraryStyles.Dramatic,
            Length = ImageAnalysisTextLengths.Brief,
            Form = ImageAnalysisTextForms.Continuous,
            Wishes = "Не описывать фон подробно"
        };

        var prompt = ImageAnalysisLiteraryPromptBuilder.BuildInitialUserPrompt(
            settings,
            "На переднем плане видна чёрная кошка рядом со свечами.");

        StringAssert.Contains(prompt, "Язык результата: English");
        StringAssert.Contains(prompt, "строго сохранять содержательную основу");
        StringAssert.Contains(prompt, "драматический");
        StringAssert.Contains(prompt, "1–2");
        StringAssert.Contains(prompt, "поле title должно быть null");
        StringAssert.Contains(prompt, settings.Wishes);
        StringAssert.Contains(prompt, "чёрная кошка");
        StringAssert.Contains(prompt, "Черновик:");
    }

    [TestMethod]
    public void InitialSystemPrompt_DefinesARealPublishingRoleWithoutProductInternals()
    {
        var prompt = ImageAnalysisLiteraryPromptBuilder.BuildInitialSystemPrompt(
            new ImageAnalysisLiterarySettings());

        StringAssert.Contains(prompt, "литературный редактор издательства");
        StringAssert.Contains(prompt, "подготовить описание изображения");
        StringAssert.Contains(prompt, "основные объекты, их расположение и взаимосвязи");
        StringAssert.Contains(prompt, "\"paragraphs\"");
        Assert.IsFalse(prompt.Contains("AI HUB", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(prompt.Contains("визуальн", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void RevisionPrompt_RequestsACompleteNewVersion()
    {
        var prompt = ImageAnalysisLiteraryPromptBuilder.BuildRevisionUserPrompt(
            new ImageAnalysisLiterarySettings { LanguageCode = "en" },
            "Видна горная долина и озеро.",
            "Тихое озеро лежит между горами.",
            "Сделай начало тревожнее.");

        StringAssert.Contains(prompt, "Сделай начало тревожнее");
        StringAssert.Contains(prompt, "Тихое озеро");
        StringAssert.Contains(prompt, "язык результата: English");
        StringAssert.Contains(prompt, "новую полную версию");
    }

    [TestMethod]
    public void NormalizeModelText_DoesNotExposeAJsonEnvelope()
    {
        var normalized = ImageAnalysisLiteraryService.NormalizeModelText(
            "{\"description\":\"Готовое описание.\",\"debug\":true}");

        Assert.AreEqual("Готовое описание.", normalized);
    }

    [TestMethod]
    public void CoreResultParser_SeparatesDescriptionFromCompactReviewSummary()
    {
        var result = ImageAnalysisCoreResultParser.Parse("""
            ```json
            {
              "title": "Ночной кот",
              "paragraphs": [
                "Чёрная кошка наблюдает за зелёным дымом.",
                "Слева горят несколько свечей."
              ],
              "review_items": [
                "Чёрная кошка — справа",
                "Чаша с зелёным дымом — внизу",
                "Горящие свечи — слева"
              ],
              "uncertainties": ["Назначение чаши неясно"]
            }
            ```
            """);

        StringAssert.Contains(result.Description, "Ночной кот");
        StringAssert.Contains(result.Description, $"Ночной кот{Environment.NewLine}{Environment.NewLine}Чёрная кошка");
        Assert.AreEqual(3, result.Summary.Items.Count);
        Assert.AreEqual("Чёрная кошка — справа", result.Summary.Items[0]);
        Assert.AreEqual(1, result.Summary.Uncertainties.Count);
        Assert.IsFalse(result.Description.Contains("review_items", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CoreResultParser_PreservesLegacyDescriptionEnvelope()
    {
        var result = ImageAnalysisCoreResultParser.Parse(
            "{\"description\":\"Старое сохранённое описание.\",\"review_items\":[]}");

        Assert.AreEqual("Старое сохранённое описание.", result.Description);
    }

    [TestMethod]
    public void CoreResultParser_PlainTextFallbackNeverUsesVisualReportData()
    {
        var result = ImageAnalysisCoreResultParser.Parse("Готовое описание без JSON.");

        Assert.AreEqual("Готовое описание без JSON.", result.Description);
        Assert.AreEqual(0, result.Summary.Items.Count);
        Assert.AreEqual(0, result.Summary.Uncertainties.Count);
    }

    [TestMethod]
    public void CoreResultParser_RejectsAnIncompleteJsonEnvelope()
    {
        Assert.ThrowsExactly<InvalidDataException>(() =>
            ImageAnalysisCoreResultParser.Parse("{\"description\":\"Оборванный текст"));
    }

    [TestMethod]
    public void ObservationExtractor_BuildsCompactItemsWithoutServiceMarkup()
    {
        var observations = ImageAnalysisObservationExtractor.Extract("""
            <think>
            analysis
            1. Главный объект — чёрная кошка справа от кадра.
            - Перед животным виден котёл с ярким зелёным дымом.
            - Слева расположены несколько горящих свечей.
            </think>
            """);

        Assert.AreEqual(3, observations.Count);
        StringAssert.Contains(observations[0], "чёрная кошка");
        StringAssert.Contains(observations[1], "зелёным дымом");
        Assert.IsFalse(observations.Any(item => item.Contains("think", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task SessionStore_PersistsVersionsAndCreatesBackupOnlyOnRequest()
    {
        var root = Path.Combine(Path.GetTempPath(), "AIHubImageAnalysisTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var sourcePath = Path.Combine(root, "source.jpg");
            await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4]);
            var storage = new StorageSettings
            {
                Results = new StorageCategorySettings
                {
                    Locations = [new StorageLocationSettings { Path = root }]
                }
            };
            var session = new ImageAnalysisLiterarySession
            {
                CurrentStep = ImageAnalysisLiterarySteps.Result,
                Status = ImageAnalysisLiteraryStatuses.ResultReady,
                File = new ImageAnalysisFilePassport
                {
                    SourcePath = sourcePath,
                    DisplayName = "source.jpg",
                    Extension = ".jpg",
                    SizeBytes = 4
                },
                Versions =
                [
                    new ImageAnalysisLiteraryVersion
                    {
                        Number = 1,
                        Text = "Литературное описание."
                    }
                ],
                Observations = ["Чёрная кошка", "Зелёный дым"],
                ReviewSummary = new ImageAnalysisReviewSummary
                {
                    Items = ["Чёрная кошка — справа", "Зелёный дым — внизу"],
                    Uncertainties = ["Назначение чаши неясно"]
                },
                Events =
                [
                    new ImageAnalysisEventEntry
                    {
                        Code = ImageAnalysisEventCodes.VisionCompleted,
                        Role = ManagedModelRoles.Vision,
                        Status = ImageAnalysisEventStatuses.Completed
                    }
                ]
            };
            session.SelectedVersionId = session.Versions[0].VersionId;
            var store = new ImageAnalysisSessionStore();

            store.Save(session, storage);
            var loaded = store.Load(session.SessionId, storage);

            Assert.IsNotNull(loaded);
            Assert.AreEqual(1, loaded.Versions.Count);
            Assert.AreEqual(2, loaded.Observations.Count);
            Assert.AreEqual(2, loaded.ReviewSummary.Items.Count);
            Assert.AreEqual(1, loaded.ReviewSummary.Uncertainties.Count);
            Assert.AreEqual(ImageAnalysisEventCodes.VisionCompleted, loaded.Events.Single().Code);
            Assert.IsTrue(string.IsNullOrWhiteSpace(loaded.InternalImageCopyPath));
            Assert.IsFalse(Directory.Exists(Path.Combine(
                store.GetProjectsDirectory(storage),
                session.SessionId,
                "Backup")));

            await store.CreateInternalBackupAsync(session, storage, CancellationToken.None);

            Assert.IsTrue(File.Exists(session.InternalImageCopyPath));
            Assert.IsTrue(File.Exists(session.InternalDescriptionCopyPath));
            StringAssert.Contains(
                await File.ReadAllTextAsync(session.InternalDescriptionCopyPath),
                "Литературное описание.");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task SessionStore_LoadsLegacySessionWithoutShowingLegacyObservationsAsSummary()
    {
        var root = Path.Combine(Path.GetTempPath(), "AIHubImageAnalysisLegacyTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var storage = new StorageSettings
            {
                Results = new StorageCategorySettings
                {
                    Locations = [new StorageLocationSettings { Path = root }]
                }
            };
            var store = new ImageAnalysisSessionStore();
            const string sessionId = "legacy-session";
            var directory = Path.Combine(store.GetProjectsDirectory(storage), sessionId);
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(
                Path.Combine(directory, "session.json"),
                """
                {
                  "schemaVersion": 1,
                  "sessionId": "legacy-session",
                  "observations": ["Сырой фрагмент старого отчёта"],
                  "events": [],
                  "versions": [],
                  "exportedFiles": []
                }
                """);

            var loaded = store.Load(sessionId, storage);

            Assert.IsNotNull(loaded);
            Assert.AreEqual(3, loaded.SchemaVersion);
            Assert.AreEqual(ImageAnalysisBundleCatalog.MediumId, loaded.BundleId);
            Assert.AreEqual(ImageAnalysisPipelineIds.Legacy, loaded.PipelineId);
            Assert.AreEqual(0, loaded.HiddenConversation.Count);
            Assert.AreEqual(1, loaded.Observations.Count);
            Assert.AreEqual(0, loaded.ReviewSummary.Items.Count);
            Assert.AreEqual(0, loaded.ReviewSummary.Uncertainties.Count);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void AssertArgumentValue(
        IReadOnlyList<string> arguments,
        string name,
        string expectedValue)
    {
        var index = arguments.ToList().IndexOf(name);
        Assert.IsTrue(index >= 0, $"Argument '{name}' is missing.");
        Assert.IsTrue(index + 1 < arguments.Count, $"Argument '{name}' has no value.");
        Assert.AreEqual(expectedValue, arguments[index + 1]);
    }
}
