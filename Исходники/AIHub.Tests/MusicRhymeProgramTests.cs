using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicRhymeProgramTests
{
    [TestMethod]
    public void ChangingTheFragmentChangeBudgetOffersMoreDistinctLinesWithoutMutatingTheSource()
    {
        const string source = "Я читаю книгу в вагоне.\nТы читаешь книгу в вагоне.";
        IReadOnlyList<RagLexiconHit> Dictionary(string word)
        {
            var tag = word switch { "читаю" => "presfut_sg1", "читаешь" => "presfut_sg2", _ => "" };
            return [new(word, "forms", JsonSerializer.SerializeToElement(new {
                forms = tag.Length == 0 ? Array.Empty<object>() : new object[] { new { form = word, tags = new[] { tag } } }
            }), false, "fixture", "CC0", "fixture", word)];
        }
        var step = Step(source, Dictionary);
        var ordinary = MusicRhymeProgram.BuildDetailed(step, [], Dictionary);
        var expanded = MusicRhymeProgram.BuildDetailed(step with { Request = step.Request with {
            Tuning = new() { Values = new() { ["program.changes_per_fragment"] = 2 } } } }, [], Dictionary);
        Assert.AreEqual(1, ordinary.Changes.Length);
        Assert.AreEqual(2, expanded.Changes.Length);
        Assert.AreEqual(2, expanded.Changes.Select(c => c.Line).Distinct().Count());
        Assert.AreEqual(source, step.Request.Source);
        var capped = MusicRhymeProgram.BuildDetailed(step with { Request = step.Request with {
            Tuning = new() { Values = new() { ["program.changes_per_fragment"] = 2, ["program.total_changes"] = 1 } } } }, [], Dictionary);
        Assert.AreEqual(1, capped.Changes.Length);
    }

    [TestMethod]
    public void LexicalFamilyAndTotalBudgetsLimitActualDictionaryEnumeration()
    {
        IReadOnlyList<RagLexiconHit> Dictionary(string word) => [new(word, "meaning", JsonSerializer.SerializeToElement(
            word == "origin" ? new { synonyms = new[] { "alpha", "beta", "gamma" } } : (object)new {
                forms = new[] { new { form = word + "form" } } }), false, "fixture", "CC0", "fixture", word)];
        var limited = new MusicRhymeTuning { Values = new() { ["search.synonym_roots"] = 1 } };
        CollectionAssert.AreEqual(new[] { "alpha", "alphaform" }, MusicRhymeVocabulary.Related("origin", Dictionary, tuning: limited).ToArray());
        var budget = new MusicRhymeTuning { Values = new() { ["search.lexical_forms"] = 2 } };
        CollectionAssert.AreEqual(new[] { "alpha", "beta" }, MusicRhymeVocabulary.Related("origin", Dictionary, tuning: budget).ToArray());
    }

    [TestMethod]
    public void EverySelectedFragmentKeepsItsOwnAlternativeWithoutTouchingOtherLines()
    {
        var subjects = new[] { "Я читаю", "Ты читаешь", "Он читает", "Мы читаем" };
        var settings = new[] { "в вагоне.", "в саду.", "у дома.", "у двери." };
        var source = string.Join("\r\n\r\n", new[] { "сейчас", "снова", "часто", "тихо" }.Select(adverb =>
            "[Verse]\r\n" + string.Join("\r\n", subjects.Select((subject, i) =>
                subject + " " + adverb + " книгу " + settings[i]))));
        IReadOnlyList<RagLexiconHit> Dictionary(string word)
        {
            var tag = word switch { "читаю" => "presfut_sg1", "читаешь" => "presfut_sg2", "читает" => "presfut_sg3", "читаем" => "presfut_pl1", _ => "" };
            var variant = word switch { "вагоне" => "ваго́не", "саду" => "саду́", "дома" => "до́ма", "двери" => "две́ри", _ => word };
            var data = JsonSerializer.SerializeToElement(new { variants = new[] { variant },
                forms = tag.Length == 0 ? Array.Empty<object>() : new object[] { new { form = word, tags = new[] { tag } } } });
            return [new(word, "forms", data, false, "fixture", "CC0", "fixture", word)];
        }
        var step = Step(source, Dictionary); var selected = MusicRhymeEditorial.ForScope(step).Fragments;
        Assert.IsGreaterThanOrEqualTo(2, selected.Length);
        var result = MusicRhymeProgram.BuildDetailed(step, [], Dictionary);
        Assert.AreEqual(selected.Length, result.Changes.Length);
        foreach (var fragment in selected) Assert.AreEqual(1, result.Changes.Count(c => fragment.Lines.Contains(c.Line)));
        Assert.AreEqual(source, step.Original); Assert.AreEqual(source, step.Request.Source);
        var expected = source;
        foreach (var change in result.Changes.OrderByDescending(c => step.Analysis!.Lines.Single(l => l.Number == c.Line).Start))
        {
            var line = step.Analysis!.Lines.Single(l => l.Number == change.Line);
            expected = expected.Remove(line.Start, line.Length).Insert(line.Start, change.After);
        }
        Assert.AreEqual(expected, result.Text);
    }

    [TestMethod]
    public void DocumentedImageShiftCanCreateAnExactRhymeWithoutBeingAStrictSynonym()
    {
        const string source = "Ты не можешь мне помочь,\r\nВетер гонит меня вдаль.";
        var step = Step(source, Lookup);
        var second = step.Analysis!.Lines.Last();
        var result = MusicRhymeProgram.BuildDetailed(step, [new(second.Number, "прочь", "про́чь", "fixture", false)], Lookup);
        Assert.AreEqual("Ты не можешь мне помочь,\r\nВетер гонит меня прочь.", result.Text);
        var edit = result.Changes.Single();
        Assert.AreEqual("ExactMeaning", edit.Reason); Assert.AreEqual("synonym-bridge", edit.Relation);
        Assert.IsFalse(edit.GrammarKnown); // Sparse invariant entries are not a claimed morphological check.
        Assert.AreEqual(source, step.Request.Source); Assert.AreEqual(source, step.Original);
    }

    [TestMethod]
    public void AnUnrelatedRhymingWordNeverBecomesAReadyProposal()
    {
        const string source = "Я хочу тебе помочь,\nВетер гонит меня вдаль.";
        var step = Step(source, Lookup); var number = step.Analysis!.Lines.Last().Number;
        var result = MusicRhymeProgram.BuildDetailed(step, [new(number, "ночь", "но́чь", "fixture", false) { SoundScore = 1 }], Lookup);
        Assert.AreEqual(source, result.Text); Assert.IsEmpty(result.Changes); Assert.IsEmpty(result.Candidates);
    }

    [TestMethod]
    public void ProgramProtectsStressQuotesAndExactScopeAndHonoursCancellation()
    {
        const string source = "[Verse]\r\nЯ читаю книгу в вагоне.\r\nВетер гонит меня вдаль.\r\n[Outro]";
        var from = source.IndexOf("Ветер", StringComparison.Ordinal); var count = "Ветер гонит меня вдаль.".Length;
        var step = Step(source, Lookup) with { Request = Step(source, Lookup).Request with { Start = from, Length = count } };
        var result = MusicRhymeProgram.BuildDetailed(step, [], Lookup);
        Assert.IsFalse(result.Text.Contains("читаю", StringComparison.Ordinal));
        foreach (var edit in result.Changes) Assert.IsTrue(edit.Before.StartsWith("Ветер", StringComparison.Ordinal));
        foreach (var text in new[] { "Ветер гонит меня вдаль+", "Ветер гонит меня вда́ль", "Я говорю: 'вдаль'." })
        {
            var protectedStep = Step(text, Lookup);
            Assert.AreEqual(text, MusicRhymeProgram.Build(protectedStep, [new(1, "прочь", "", "fixture", false)], Lookup));
        }
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => MusicRhymeProgram.BuildDetailed(step, [], Lookup, cancellation.Token));
    }

    [TestMethod]
    public void AddedEvidenceAndChangeDescriptionsRoundTripAndOldStepsStayReadable()
    {
        var old = Step("Кот идёт\nКот поёт", _ => []);
        var copy = JsonSerializer.Deserialize<MusicRhymeStep>(JsonSerializer.Serialize(old))!;
        Assert.IsEmpty(copy.ProgramChanges); copy.Validate();
        var current = old with { ProgramChanges = [new(1, "before", "after", "Semantic")],
            Suggestions = [new(1, "word", "reading", "fixture", false) { MeaningScore = .7, SoundScore = .8 }] };
        Assert.AreEqual(current.ProgramChanges[0], current.Snapshot().ProgramChanges[0]);
        Assert.AreEqual(.8, current.Snapshot().Suggestions[0].SoundScore);
    }

    [TestMethod]
    public void OneLongInflectionTableCannotHideAllLaterSynonymFamilies()
    {
        IReadOnlyList<RagLexiconHit> Dictionary(string word)
        {
            object data = word switch {
                "origin" => new { synonyms = new[] { "alpha", "beta" } },
                "alpha" => new { forms = Enumerable.Range(0, 48).Select(i => new {
                    form = "alpha" + (char)('a' + i / 26) + (char)('a' + i % 26) }).ToArray() },
                "beta" => new { forms = new[] { new { form = "betaword" } } }, _ => new { } };
            return [new(word, "meaning", JsonSerializer.SerializeToElement(data), false, "fixture", "CC0", "fixture", word)];
        }
        var alternatives = MusicRhymeVocabulary.Related("origin", Dictionary).Take(48).ToArray();
        CollectionAssert.Contains(alternatives, "beta"); CollectionAssert.Contains(alternatives, "betaword");
    }

    [TestMethod]
    public Task WorkshopDisplaysTheActualAlternativeAndItsLocalizedReason() => ScenarioNavigationTests.Sta(() =>
    {
        foreach (var language in new[] { "ru", "en" })
        {
            var localization = new LocalizationService(); localization.Load(language);
            using var view = new MusicWorkspaceControl(); view.Localize(localization.T);
            const string source = "Ты не можешь мне помочь,\nВетер гонит меня вдаль.";
            view.Editor.Lyrics = source;
            var step = Step(source, Lookup); step = step with { Request = step.Request with { ProjectId = view.Projects.Current.Id } };
            var result = MusicRhymeProgram.BuildDetailed(step, [new(2, "прочь", "про́чь", "fixture", false)], Lookup);
            view.Rhyme.Accept(step with { Program = result.Text, ProgramDraft = result.Text, ProgramChanges = result.Changes,
                ProgramReady = true, ModelReady = true, Stage = "Ready", Model = result.Text, Reply = result.Text });
            var host = new Window();
            host.Resources["TextPrimaryBrush"] = Brushes.White;
            host.Resources["TextSecondaryBrush"] = Brushes.LightGray; host.Resources["PanelBrush"] = Brushes.MidnightBlue;
            host.Resources["WindowBackgroundBrush"] = Brushes.Black; host.Resources["UiBodyFontSize"] = 14d;
            host.Resources["SecondaryButtonBackgroundBrush"] = Brushes.Black;
            host.Resources["LineBrush"] = Brushes.Gray; host.Resources["AccentBrush"] = Brushes.RoyalBlue;
            var window = new MusicRhymeWindow(view.Rhyme, view.Projects.Current.Id, localization.T, host.Resources);
            try
            {
                window.Show(); window.UpdateLayout();
                var description = Descendants(window).OfType<TextBlock>()
                    .Single(t => AutomationProperties.GetAutomationId(t) == "Music.Rhyme.ProgramChanges").Text;
                StringAssert.Contains(description, "Ветер гонит меня прочь.");
                StringAssert.Contains(description, localization.T("Music.Rhyme.Change.ExactMeaning"));
                StringAssert.Contains(description, localization.T("Music.Rhyme.GrammarUnverified"));
                Assert.AreEqual(source, view.Editor.Lyrics);
                if (Environment.GetEnvironmentVariable("AIHUB_RHYME_PROGRAM_UI") is { Length: > 0 } directory)
                {
                    Directory.CreateDirectory(directory);
                    var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(directory, language + ".png")); encoder.Save(file);
                }
            }
            finally { window.Close(); host.Close(); }
        }
    });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    [TestMethod, TestCategory("RhymeRuntime")]
    public async Task RealDictionaryProgramFlowsIntoTheModelTaskWithoutChangingTheSource()
    {
        if (Environment.GetEnvironmentVariable("LOPATA_RHYME_RUNTIME") != "1")
        { Assert.Inconclusive("Opt-in installed dictionary and mock model test"); return; }
        Assert.IsTrue(Directory.Exists(MusicRhymeCorpus.DirectoryPath));
        const string source = "[Verse]\nЯ открываю словарь на экране,\nКаждое слово — новая игра.";
        var step = Step(source, _ => []); var model = new CaptureModel(source);
        step = step with { Request = step.Request with { ModelPath = Path.Combine(Path.GetTempPath(), "mock.gguf") },
            Analysis = null, Reference = "{}" };
        var checkpoints = new List<MusicRhymeStep>();
        var result = await new MusicRhymeWork(() => model).RunAsync(step, s => { checkpoints.Add(s.Snapshot()); return s; },
            new InlineProgress<RagBookProgress>(_ => { }), new CancellationTokenSource(TimeSpan.FromMinutes(2)).Token);
        Assert.IsTrue(result.ProgramReady); Assert.IsTrue(result.ModelReady, result.Error);
        Assert.IsNotEmpty(result.ProgramChanges); Assert.AreNotEqual(source, result.ProgramDraft);
        Assert.AreEqual(source, result.Request.Source); Assert.AreEqual(source, result.Original);
        Assert.IsTrue(checkpoints.Any(s => s.ProgramReady && s.ProgramChanges.Length > 0 && !s.ModelReady));
        using var prompt = JsonDocument.Parse(model.Prompt);
        Assert.AreEqual(result.ProgramDraft, prompt.RootElement.GetProperty("program_candidate").GetString());
    }

    private sealed class CaptureModel(string source) : IMusicRhymeModel
    {
        public string Prompt { get; private set; } = "";
        public Task<string> RewriteAsync(DebugModelInfo model, string prompt, CancellationToken token)
        { Prompt = prompt; return Task.FromResult(source); }
        public void Dispose() { }
    }

    private static MusicRhymeStep Step(string source, Func<string, IReadOnlyList<RagLexiconHit>> lookup) => new() {
        Request = new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), source, 0, source.Length, "core", null, "", "ru"),
        Original = source, Analysis = MusicRhymeAnalyzer.Analyze(source, lookup) };
    private static IReadOnlyList<RagLexiconHit> Lookup(string word)
    {
        var json = MusicRhymePhonetics.Clean(word) switch {
            "помочь" => """{"variants":["помо́чь"]}""",
            "вдаль" => """{"variants":["вда́ль"],"forms":[],"translations":["far"]}""",
            "прочь" => """{"variants":["про́чь"],"forms":[],"translations":["away, off"]}""",
            "далеко" => """{"forms":[],"translations":["far off, a great distance away"]}""", _ => "{}" };
        using var document = JsonDocument.Parse(json);
        var hits = new List<RagLexiconHit> { new(word, "forms", document.RootElement.Clone(), false, "fixture", "CC0", "fixture", word) };
        if (word == "вдаль") hits.Add(new(word, "meaning", JsonSerializer.SerializeToElement(new { synonyms = new[] { "далеко" } }), false, "fixture", "CC0", "fixture", word));
        return hits;
    }
}
