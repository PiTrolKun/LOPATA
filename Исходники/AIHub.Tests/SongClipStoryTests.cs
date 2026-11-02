using System.IO;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.SongClip;
using AIHub.Services.VideoPreparation;

namespace AIHub.Tests;

[TestClass]
public sealed class SongClipStoryTests
{
    private const string Anchor = "Song-inspired visual motifs: a cat looking into a bowl.";
    private const string Extracted = "{\"subjects\":[{\"sourceLine\":\"Кот смотрит в миску.\",\"visual\":\"a cat looking into a bowl\"}]}";
    private static readonly OutputModelUse Model = new("core", "Test core", "pinned", "song-to-clip semantic planning");

    [TestMethod]
    public void ModelSeesReadableOriginalLanguageInsteadOfNestedUnicodeEscapeSequences()
    {
        var attempt = Attempt();
        var meaning = SongClipStoryBrief.Input(attempt);
        var scenes = SongClipVisualProtocol.BuildInput(attempt, 20);
        foreach (var body in new[] { meaning, scenes }) {
            Assert.Contains("Кот смотрит в миску.", body);
            Assert.DoesNotContain("\\u041A", body);
            // The HTTP envelope must decode to the same readable inner document.
            using var envelope = JsonDocument.Parse(JsonSerializer.Serialize(new { content = body }));
            Assert.AreEqual(body, envelope.RootElement.GetProperty("content").GetString());
        }
    }

    [TestMethod]
    public async Task MeaningIsPreparedWithoutMusicalStyleAndBecomesMandatoryRenderContext()
    {
        using var files = new VideoPreparationTests.TestFiles(); var job = files.File("job"); var calls = 0;
        var attempt = Attempt();
        var plan = await SongClipPlanning.EnsureCoreAsync(attempt, job, 20, "input", (_, body, _, _) => {
            calls++; using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("actualLyricLines", out var lines)) {
                Assert.IsTrue(lines.GetArrayLength() > 0);
                Assert.IsFalse(body.Contains("punk", StringComparison.Ordinal));
                return Task.FromResult(new VideoStructuredAnswer(Extracted, Model));
            }
            Assert.AreEqual(Anchor, json.RootElement.GetProperty("semanticAnchor").GetString());
            return Task.FromResult(Scenes(json.RootElement));
        }, (_, _) => { }, CancellationToken.None, prepareStory: true);
        Assert.AreEqual(2, calls); Assert.IsTrue(plan.GlobalStyle.StartsWith(Anchor, StringComparison.Ordinal));
        Assert.AreEqual(Model, plan.Assistants.Single());
        var reused = await SongClipPlanning.EnsureCoreAsync(attempt, job, 20, "input", (_, _, _, _) =>
            throw new AssertFailedException("A completed plan must not reload its interpretation or model."), (_, _) => { }, CancellationToken.None, prepareStory: true);
        Assert.AreEqual(plan.GlobalStyle, reused.GlobalStyle);
    }

    [TestMethod]
    public async Task CancellationAfterMeaningKeepsItAndResumeDoesNotInterpretAgain()
    {
        using var files = new VideoPreparationTests.TestFiles(); var job = files.File("job"); using var cancel = new CancellationTokenSource();
        var attempt = Attempt();
        await Assert.ThrowsAsync<OperationCanceledException>(() => SongClipPlanning.EnsureCoreAsync(attempt, job, 20, "input",
            (_, _, _, _) => { cancel.Cancel(); return Task.FromResult(new VideoStructuredAnswer(Extracted, Model)); },
            (_, _) => { }, cancel.Token, prepareStory: true));
        Assert.IsTrue(File.Exists(Path.Combine(job, "visual-story.json")));
        _ = await SongClipPlanning.EnsureCoreAsync(attempt, job, 20, "input", (_, body, _, _) => {
            using var json = JsonDocument.Parse(body); Assert.IsFalse(json.RootElement.TryGetProperty("actualLyricLines", out _));
            return Task.FromResult(Scenes(json.RootElement));
        }, (_, _) => { }, CancellationToken.None, prepareStory: true);
    }

    [TestMethod]
    [DataRow("{\"visualAnchor\":\"Кот смотрит в миску и видит отражение.\"}")]
    [DataRow("{\"visualAnchor\":\"\"}")]
    [DataRow("{\"visualAnchor\":\"A cat studies the bowl under a dim lamp.\",\"untrusted\":1}")]
    [DataRow("{\"subjects\":[{\"sourceLine\":\"Нет такой строки\",\"visual\":\"a cat\"}]}")]
    public void MeaningRejectsIncompleteOrInvalidEnglish(string raw) => Assert.Throws<InvalidDataException>(() => SongClipStoryBrief.Parse(raw, Attempt()));

    [TestMethod]
    public void NativeMeaningSchemaRestrictsSourceLinesAndKeepsReferenceCastSeparate()
    {
        var legacy = Attempt();
        var options = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All) };
        Assert.AreEqual(JsonSerializer.Serialize(new { songTitle = legacy.Name, actualLyricLines = SongClipStoryBrief.Lines(legacy) }, options),
            SongClipStoryBrief.Input(legacy));
        var attempt = legacy with { Reference = "singer.png", References = [new("singer.png", "performer", "Певица поёт рядом с котом.")] };
        var roles = new[] { new SongClipReferenceRole(1, "performer", "The singer performs the song beside the cat.") };
        using var body = JsonDocument.Parse(SongClipStoryBrief.Input(attempt, roles));
        Assert.AreEqual(roles[0].Description, body.RootElement.GetProperty("referenceRoles")[0].GetProperty("description").GetString());
        Assert.IsFalse(body.RootElement.TryGetProperty("references", out _));
        Assert.DoesNotContain("singer.png", body.RootElement.GetRawText());
        Assert.Contains("Never erase, replace or reinterpret a referenced performer", SongClipStoryBrief.SystemFor(roles));
        using var schema = JsonDocument.Parse(JsonSerializer.Serialize(SongClipStoryBrief.Schema(attempt)));
        var choices = schema.RootElement.GetProperty("json_schema").GetProperty("schema").GetProperty("properties")
            .GetProperty("subjects").GetProperty("items").GetProperty("properties").GetProperty("sourceLine").GetProperty("enum");
        CollectionAssert.AreEqual(SongClipStoryBrief.Lines(attempt), choices.EnumerateArray().Select(row => row.GetString()!).ToArray());
    }

    [TestMethod]
    public async Task MalformedMeaningIsSavedBeforeOneRepairAndTheConfirmedMeaningIsReused()
    {
        using var files = new VideoPreparationTests.TestFiles(); var job = files.File("story-repair"); var calls = 0;
        var attempt = Attempt(); const string invalid = "{\"subjects\":[{\"sourceLine\":\"Сокращённая строка\",\"visual\":\"a cat\"}]}";
        var story = await SongClipStoryPlanning.EnsureAsync(attempt, job, "input", new(null, null), (system, _, _, _) => {
            calls++; if (calls == 2) Assert.Contains("do not shorten, translate or paraphrase", system);
            return Task.FromResult(new VideoStructuredAnswer(calls == 1 ? invalid : Extracted, Model));
        }, (_, _) => { }, CancellationToken.None);
        Assert.AreEqual(2, calls); Assert.AreEqual(Anchor, story.Anchor);
        foreach (var index in new[] { 1, 2 }) {
            using var raw = JsonDocument.Parse(File.ReadAllText(Path.Combine(job, $"planner-story-{index}.json")));
            Assert.AreEqual(index == 1 ? invalid : Extracted, raw.RootElement.GetProperty("Text").GetString());
            Assert.AreEqual(Model.Id, raw.RootElement.GetProperty("Model").GetProperty("Id").GetString());
        }
        var reused = await SongClipStoryPlanning.EnsureAsync(attempt, job, "input", new(null, null), (_, _, _, _) =>
            throw new AssertFailedException("A confirmed story must not call the model again."), (_, _) => { }, CancellationToken.None);
        Assert.AreEqual(story, reused);
    }

    [TestMethod]
    public async Task StoryRepairIsBoundedAndDoesNotRetryOperationalFailures()
    {
        using var files = new VideoPreparationTests.TestFiles(); var calls = 0; var job = files.File("story-invalid");
        await Assert.ThrowsAsync<InvalidDataException>(() => SongClipStoryPlanning.EnsureAsync(Attempt(), job, "input", new(null, null),
            (_, _, _, _) => { calls++; return Task.FromResult(new VideoStructuredAnswer("{}", Model)); }, (_, _) => { }, CancellationToken.None));
        Assert.AreEqual(2, calls); Assert.IsFalse(File.Exists(Path.Combine(job, "visual-story.json")));
        Assert.IsTrue(File.Exists(Path.Combine(job, "planner-story-2.json")));
        calls = 0;
        await Assert.ThrowsAsync<IOException>(() => SongClipStoryPlanning.EnsureAsync(Attempt(), files.File("story-io"), "input", new(null, null),
            (_, _, _, _) => { calls++; throw new IOException("operational failure"); }, (_, _) => { }, CancellationToken.None));
        Assert.AreEqual(1, calls);
    }

    private static SongClipAttempt Attempt() => new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "song.wav", "", "Cat",
        "Style:\npunk rock\nLyrics:\nКот смотрит в миску.", "", new() { Interval = 10, Prompt = "" }, "unused", 8) { PlanningVersion = 1 };
    private static VideoStructuredAnswer Scenes(JsonElement input) => new(JsonSerializer.Serialize(new {
        globalStyle = "A gritty apartment with the same orange cat and expressive soft window lighting.",
        scenes = input.GetProperty("slots").EnumerateArray().Select(row => new {
            index = row.GetProperty("index").GetInt32(), startSeconds = row.GetProperty("startSeconds").GetDouble(),
            endSeconds = row.GetProperty("endSeconds").GetDouble(),
            prompt = "An orange cat studies a bowl in an old room under soft light, composition " + row.GetProperty("index").GetInt32(),
            lyricAnchors = new[] { "Кот смотрит в миску." }
        }).ToArray()
    }), Model);
}
