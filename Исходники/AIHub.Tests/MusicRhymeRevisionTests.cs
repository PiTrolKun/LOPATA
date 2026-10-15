using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicRhymeRevisionTests
{
    [TestMethod]
    public void UnmatchedVerseIsReviewedWithoutForcingARhymeSchemeOnIntro()
    {
        const string song = "[Intro]\r\nЛа-ла... Ла-ла...\r\nНачинаем рассказ.\r\n\r\n[Verse 1]\r\n"
            + "За стенами мерцает сад,\r\nНад крышей слышен звон.\r\nНа полке дремлет старый кот,\r\nЗатих пустынный зал.\r\n"
            + "Седой туман укутал пруд,\r\nВдали блеснула сталь.\r\nСмотрю на мир, где правит мрак,\r\nИ медленно редеет лес.\r\n"
            + "\r\n[Outro]\r\nЛа-ла.";
        var step = Step(song);
        var intro = step.Analysis!.Lines.Where(l => l.Performance).ToArray();
        Assert.AreEqual(3, intro.Length);
        Assert.IsFalse(step.Analysis.Issues.Any(i => intro.Any(l => l.Number == i.Line)));
        var verse = step.Analysis.Lines.Where(l => !l.Performance).ToArray();
        Assert.AreEqual(8, verse.Length);
        foreach (var line in verse) Assert.IsTrue(step.Analysis.Issues.Any(i => i.Line == line.Number && i.Kind == "Review"));
        Assert.AreEqual(8, MusicRhymeWork.EditableLines(step).Count);
        Assert.IsFalse(step.Analysis.Issues.Any(i => i.Kind == "Rhyme")); // No invented pair for dictionary suggestions.
        Assert.IsFalse(MusicRhymeTask.SearchQuery(step).Contains("English", StringComparison.Ordinal));
    }

    [TestMethod]
    public void StanzaContextCanBeEditedButUserSelectionAndDirectionsStayProtected()
    {
        const string song = "[Verse]\nСпит кот\nБродит свод\nБелый сад\nВидит мрак\n\n[Outro]\nПока!";
        var step = Step(song);
        var editable = MusicRhymeWork.EditableLines(step);
        CollectionAssert.AreEquivalent(new[] { 1, 2, 3, 4 }, editable.ToArray());
        MusicRhymeReply.Validate(song, song.Replace("Бродит свод", "Виден лёд", StringComparison.Ordinal), editable);
        var start = song.IndexOf("Белый", StringComparison.Ordinal);
        var fragment = step with { Request = step.Request with { Start = start, Length = "Белый сад".Length } };
        CollectionAssert.AreEquivalent(new[] { 0 }, MusicRhymeWork.EditableLines(fragment).ToArray());
        using var prompt = JsonDocument.Parse(MusicRhymeTask.Prompt(fragment));
        Assert.AreEqual(3, prompt.RootElement.GetProperty("stanza_context").GetArrayLength());
        Assert.Throws<InvalidDataException>(() => MusicRhymeReply.Validate("Кот (тихо)\nСад", "Кот (громко)\nСвод", new HashSet<int> { 0, 1 }));
        var aside = MusicRhymeAnalyzer.Analyze("Спит кот (тихо)\nБелый сад", _ => []);
        Assert.AreEqual("кот", aside.Lines[0].Ending);
    }

    [TestMethod]
    public void PromptDoesNotRepeatAnUnchangedSoftwareDraftOrExistingStanzaText()
    {
        var step = Step("Спит кот\nБелый сад");
        step = step with { ProgramDraft = step.Request.Text };
        using var prompt = JsonDocument.Parse(MusicRhymeTask.Prompt(step));
        Assert.AreEqual(JsonValueKind.Null, prompt.RootElement.GetProperty("program_candidate").ValueKind);
        Assert.AreEqual(0, prompt.RootElement.GetProperty("stanza_context").GetArrayLength());
        var crlf = Step("Спит кот\r\nБелый сад") with { ProgramDraft = "Спит кот\nБелый сад" };
        using var normalized = JsonDocument.Parse(MusicRhymeTask.Prompt(crlf));
        Assert.AreEqual(JsonValueKind.Null, normalized.RootElement.GetProperty("program_candidate").ValueKind);
    }

    [TestMethod, TestCategory("RhymeRuntime")]
    public async Task UserSongProducesAnActualEditorialAlternative()
    {
        var input = Environment.GetEnvironmentVariable("LOPATA_RHYME_STEPS");
        if (Environment.GetEnvironmentVariable("LOPATA_RHYME_RUNTIME") != "1" || input is null)
        { Assert.Inconclusive("Opt-in user-song test"); return; }
        var saved = JsonSerializer.Deserialize<MusicRhymeStep[]>(File.ReadAllText(input))![^1];
        var request = saved.Request with { Id = Guid.NewGuid().ToString("N"), ProjectId = Guid.NewGuid().ToString("N") };
        if (Environment.GetEnvironmentVariable("LOPATA_RHYME_MODEL") is { } modelId)
        {
            var settings = new StorageSettingsStore().LoadOrCreate();
            var selected = MusicRhymePreferences.Choices(settings).Single(c => c.Candidate.Id == modelId).Model!;
            request = request with { ModelId = modelId, ModelPath = selected.Path };
        }
        var focused = Environment.GetEnvironmentVariable("LOPATA_RHYME_FRAGMENT") == "1";
        if (focused)
        {
            var verse = System.Text.RegularExpressions.Regex.Match(request.Source, @"(?im)^\[Verse[^\]]*\][\t ]*\r?\n");
            if (!verse.Success) throw new InvalidDataException("The private test input needs a verse section.");
            var start = verse.Index + verse.Length;
            var boundary = System.Text.RegularExpressions.Regex.Match(request.Source[start..], @"\r?\n\s*\r?\n|\r?\n\[");
            var end = boundary.Success ? start + boundary.Index : request.Source.Length;
            var fragment = request.Source[start..end].TrimEnd('\r', '\n');
            request = request with { Start = start, Length = fragment.Length };
        }
        var watch = System.Diagnostics.Stopwatch.StartNew(); var events = new List<object>();
        var output = Environment.GetEnvironmentVariable("LOPATA_MUSIC_UI_EVIDENCE")!; Directory.CreateDirectory(output);
        var raw = 0;
        var result = await new MusicRhymeWork(() => new MusicRhymeModel(new(new UserProfileStore(), new IpLocationService()),
                response => File.WriteAllText(Path.Combine(output, "native-" + Interlocked.Increment(ref raw) + ".json"), response)))
            .RunAsync(new() { Request = request, Original = request.Text,
                Reference = Environment.GetEnvironmentVariable("LOPATA_RHYME_NO_REFERENCE") == "1" ? "{}" : "" }, s => s,
                new InlineProgress<RagBookProgress>(p => { lock (events) {
                    var item = new { elapsed = watch.Elapsed.TotalSeconds, p.Stage, p.Done, p.Total }; events.Add(item);
                    File.AppendAllText(Path.Combine(output, "stages.jsonl"), JsonSerializer.Serialize(item) + "\n");
                } }),
                new CancellationTokenSource(TimeSpan.FromMinutes(20)).Token);
        File.WriteAllText(Path.Combine(output, focused ? "focused-verse-result.json" : "user-song-result.json"), JsonSerializer.Serialize(new { seconds = watch.Elapsed.TotalSeconds, result, events },
            new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Assert.IsTrue(result.ModelReady, result.Error);
        Assert.IsFalse(result.ModelSkipped);
        Assert.IsTrue(result.Stage == "Ready" || result.Error == "Music.Rhyme.Unresolved", result.Error);
        Assert.AreNotEqual(result.Original.Replace("\r\n", "\n", StringComparison.Ordinal), result.Reply.Replace("\r\n", "\n", StringComparison.Ordinal));
        var stanza = result.Analysis!.Lines.First(l => !l.Performance && l.Start >= request.Start).Stanza;
        var firstVerse = result.Analysis.Lines.Where(l => l.Stanza == stanza).ToArray();
        Assert.AreEqual(8, firstVerse.Length);
        var editable = MusicRhymeWork.EditableLines(result);
        Assert.IsTrue(firstVerse.All(l => editable.Contains(System.Text.RegularExpressions.Regex.Matches(
            request.Source.Substring(request.Start, l.Start - request.Start), @"\r\n|\r|\n").Count)));
        MusicRhymeReply.Validate(request.Text, result.Reply, MusicRhymeWork.EditableLines(result));
    }

    private static MusicRhymeStep Step(string song) => new() {
        Request = new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), song, 0, song.Length, "core", null, "", "ru"),
        Original = song, Reference = "{}", Analysis = MusicRhymeAnalyzer.Analyze(song, _ => []) };
}
