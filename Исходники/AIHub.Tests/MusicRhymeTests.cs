using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicRhymeTests
{
    [TestMethod]
    public void PhoneticsUsesStressAndPronunciationInsteadOfSpelling()
    {
        Assert.AreEqual(MusicRhymePhonetics.Russian("пло́д")!.Key, MusicRhymePhonetics.Russian("ко́т")!.Key);
        Assert.AreNotEqual(MusicRhymePhonetics.Russian("за́мок")!.Key, MusicRhymePhonetics.Russian("замо́к")!.Key);
        Assert.IsNull(MusicRhymePhonetics.Russian("молоко"));
        Assert.AreEqual(MusicRhymePhonetics.Russian("молоко́")!.Key, MusicRhymePhonetics.Russian("молок+о")!.Key);
        Assert.AreEqual(MusicRhymePhonetics.English("night", ["N", "AY1", "T"])!.Key,
            MusicRhymePhonetics.English("light", ["L", "AY1", "T"])!.Key);
        Assert.AreEqual(2, MusicRhymePhonetics.English("reading", ["R", "IY1", "D", "IH0", "NG"])!.Syllables);
    }
    [TestMethod]
    public void AnalysisPreservesOffsetsAndSeparatesCrLfStanzasAndUnknownStress()
    {
        const string source = "[Verse]\r\nСпит кот\r\nВидит сад\r\n\r\n[Chorus]\r\nТам мрак\r\nТам враг\r\nМолоко";
        var result = MusicRhymeAnalyzer.Analyze(source, _ => []);
        foreach (var line in result.Lines) Assert.AreEqual(line.Text, source.Substring(line.Start, line.Length));
        Assert.AreEqual(5, result.Lines.Length);
        Assert.AreNotEqual(result.Lines[1].Stanza, result.Lines[2].Stanza);
        Assert.AreEqual(2, result.Issues.Count(i => i.Kind == "Rhyme"));
        Assert.IsTrue(result.Issues.Any(i => i.Line == 5 && i.Kind == "Uncertain"));
        Assert.IsFalse(result.Issues.Any(i => i.Line == 5 && i.Kind == "Rhyme"));
        Assert.AreEqual(2, MusicRhymeAnalyzer.Analyze("{Император}\nСпит кот\n(Король)\nВидит сад", _ => []).Lines.Length);
    }
    [TestMethod]
    public void FragmentAndReplyGuardsProtectSurroundingLinesTagsAndUserStress()
    {
        const string source = "[Verse]\r\nБелый кот\r\nСмотрит в сад\r\n[Chorus]";
        var at = source.IndexOf("Смотрит", StringComparison.Ordinal);
        var scope = MusicRhymeAnalyzer.Scope(new(source, at + 2, 3), true);
        Assert.AreEqual(at, scope.Start); Assert.AreEqual("Смотрит в сад", source.Substring(scope.Start, scope.Length));
        Assert.Throws<InvalidDataException>(() => MusicRhymeAnalyzer.Scope(new(source, 0, 0), true));
        MusicRhymeReply.Validate("[Verse]\nКот\nСад", "[Verse]\nКот\nСвод", new HashSet<int> { 2 });
        Assert.Throws<InvalidDataException>(() => MusicRhymeReply.Validate("[Verse]\nКот\nСад", "[Verse]\nПёс\nСвод", new HashSet<int> { 2 }));
        Assert.Throws<InvalidDataException>(() => MusicRhymeReply.Validate("[Verse]\nКо́т", "[Chorus]\nКо́т", new HashSet<int> { 0, 1 }));
        Assert.Throws<InvalidDataException>(() => MusicRhymeReply.Validate("Ко́т", "Кот", new HashSet<int> { 0 }));
    }
    [TestMethod]
    public void PreferencesExcludeHydraAndKeepCoreAsIndependentDefault()
    {
        Assert.IsFalse(MusicRhymePreferences.Allowed("profile-hydra")); Assert.IsTrue(MusicRhymePreferences.Allowed("core"));
        Assert.AreEqual("core", new MusicRhymeOptions().ModelId); Assert.AreEqual("#36B859", new MusicRhymeOptions().Color);
        var request = Request("Кот\nСад") with { ModelId = "profile-hydra" };
        Assert.Throws<InvalidDataException>(request.Validate);
    }
    [TestMethod]
    public void SoftwareInversionKeepsAllWordsPunctuationAndTheOtherLine()
    {
        const string source = "[Verse]\r\nСпит кот\r\nА за окном цветёт мой сад.";
        var request = Request(source);
        var step = new MusicRhymeStep { Request = request, Original = source, Analysis = MusicRhymeAnalyzer.Analyze(source, _ => []) };
        var result = MusicRhymeProgram.Build(step, [], _ => []);
        Assert.AreEqual("[Verse]\r\nСпит кот\r\nА за окном мой сад цветёт.", result);
        var after = MusicRhymeAnalyzer.Analyze(result, _ => []);
        Assert.IsFalse(after.Issues.Any(i => i.Kind == "Rhyme"));
        foreach (var newline in new[] { "\r", "\n", "\r\n" })
            Assert.AreEqual(2, MusicRhymeAnalyzer.Analyze("Спит кот" + newline + "Видит сад", _ => []).Lines.Length);
    }
    [TestMethod]
    public Task WorkshopKeepsEditsAcrossStagesAndCopiesWithoutApplyingToSong() => ScenarioNavigationTests.Sta(() =>
    {
        using var view = new MusicWorkspaceControl(); view.Editor.Lyrics = "Кот\nСад";
        var request = Request(view.Editor.Lyrics) with { ProjectId = view.Projects.Current.Id };
        var step = new MusicRhymeStep { Request = request, Original = request.Text };
        view.Rhyme.Accept(step); view.Rhyme.Edit(request.ProjectId, request.Id, 1, "Моя правка");
        var merged = view.Rhyme.Accept(step with { Program = "Поздний ответ", ProgramReady = true });
        Assert.AreEqual("Моя правка", merged.Program); Assert.AreEqual("Кот\nСад", view.Editor.Lyrics);
        var saved = view.Rhyme.SnapshotHistory(); saved[0] = saved[0] with { Original = "Другой" };
        Assert.AreEqual("Кот\nСад", view.Rhyme.History[0].Original);
        Assert.AreEqual(0, view.Projects.Capture().RhymeSteps.Length); // A draft is not a submitted generation.
    });
    [TestMethod]
    public Task SubmissionPersistsWorkshopAndProjectSwitchKeepsItIsolated() => ScenarioNavigationTests.Sta(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-rhyme-project-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var projects = new MusicProjects(Path.Combine(root, "projects")); var jobs = new MusicGenerationJobs(Path.Combine(root, "jobs"));
            using var view = new MusicWorkspaceControl(projects, jobs); view.Editor.Lyrics = "Кот\nСад";
            var request = Request(view.Editor.Lyrics) with { ProjectId = view.Projects.Current.Id };
            view.Rhyme.Accept(new() { Request = request, Original = request.Text, Program = "Кот\nСвод", ProgramReady = true });
            Assert.AreEqual(0, projects.List().Count);
            var job = jobs.Create(root, Path.Combine(root, "audio"), "Test", 1, 30, "rock", view.Editor.Lyrics);
            view.Projects.Record(job, view.Projects.Capture());
            var persisted = projects.Load(request.ProjectId); Assert.AreEqual("Кот\nСвод", persisted.Steps.Single().Snapshot.RhymeSteps.Single().Program);
            view.Projects.New(); Assert.AreEqual(0, view.Rhyme.History.Count);
            view.Projects.Open(request.ProjectId); Assert.AreEqual(request.Id, view.Rhyme.History.Single().Request.Id);
            view.Rhyme.Edit(request.ProjectId, request.Id, 1, "Новый набросок");
            Assert.AreEqual("Кот\nСвод", projects.Load(request.ProjectId).Saved.RhymeSteps.Single().Program);
            projects.SaveWorkspace(view.Projects.Current, view.Projects.Capture());
            Assert.AreEqual("Кот\nСвод", projects.Load(request.ProjectId).Saved.RhymeSteps.Single().Program);
        }
        finally
        {
            var full = Path.GetFullPath(root);
            if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(full).StartsWith("lopata-rhyme-project-", StringComparison.Ordinal)) throw new InvalidOperationException();
            Directory.Delete(full, true);
        }
    });
    [TestMethod]
    public Task GlyphColourAndWorkshopLayoutsKeepNativeTextAndUndo() => ScenarioNavigationTests.Sta(() =>
    {
        var l = new LocalizationService();
        foreach (var language in new[] { "ru", "en" }) foreach (var dark in new[] { false, true })
        {
            l.Load(language); using var view = new MusicWorkspaceControl(); view.Localize(l.T);
            var request = Request("[Verse]\nСпит кот\nВидит сад") with { ProjectId = view.Projects.Current.Id };
            var analysis = MusicRhymeAnalyzer.Analyze(request.Source, _ => []);
            view.Rhyme.Accept(new() { Request = request, Original = request.Text, Program = request.Text, Model = request.Text,
                Analysis = analysis, ProgramReady = true, ModelReady = true, Stage = "Ready" });
            // Real windows do not inherit owner resources automatically. Never inject a theme into the child here.
            var themeHost = new Window(); Theme(themeHost, dark);
            var window = new MusicRhymeWindow(view.Rhyme, request.ProjectId, l.T, themeHost.Resources);
            Modal(window, () => {
                var editors = Desc<TextBox>(window).Where(x => AutomationProperties.GetAutomationId(x).StartsWith("Music.Rhyme.Column.")).ToArray();
                Assert.AreEqual(3, editors.Length); Assert.IsTrue(editors.All(e => !e.IsReadOnly && e.ActualWidth > 150));
                Assert.IsTrue(window.Resources.MergedDictionaries.Contains(themeHost.Resources));
                Assert.AreSame(themeHost.Resources["WindowBackgroundBrush"], window.Background);
                foreach (var editor in Desc<TextBox>(window)) Assert.AreSame(themeHost.Resources["TextPrimaryBrush"], editor.Foreground);
                foreach (var width in new[] { 700d, 1250d })
                {
                    window.Width = width; window.UpdateLayout();
                    Assert.IsLessThanOrEqualTo(1d, editors.Max(e => e.ActualHeight) - editors.Min(e => e.ActualHeight));
                    Assert.IsLessThanOrEqualTo(1d, editors.Max(e => e.TranslatePoint(new(), window).Y) - editors.Min(e => e.TranslatePoint(new(), window).Y));
                }
                editors[1].AppendText("!"); editors[1].Undo(); Assert.AreEqual(request.Text.Replace("\n", "\r\n"), editors[1].Text.Replace("\r\n", "\n").Replace("\n", "\r\n"));
                Snapshot(window, "workshop-" + language + "-" + dark);
                Theme(themeHost, !dark); window.UpdateLayout();
                foreach (var editor in Desc<TextBox>(window)) Assert.AreSame(themeHost.Resources["TextPrimaryBrush"], editor.Foreground);
            });
            using var songEditor = new MusicLyricsEditor();
            var host = new Window { Width = 450, Height = 500, Content = songEditor }; Theme(host, dark);
            Modal(host, () => {
                songEditor.Lyrics = request.Source; var editor = Desc<TextBox>(songEditor).Single(); var original = editor.Text;
                editor.Select(3, 0); songEditor.SetRhymeAnalysis(analysis, "#36B859"); host.UpdateLayout();
                Snapshot(host, "colour-" + language + "-" + dark);
                Assert.AreEqual(original, editor.Text); Assert.AreEqual(3, editor.SelectionStart);
                editor.AppendText("!"); editor.Undo(); Assert.AreEqual(original, editor.Text);
            });
        }
    });
    [TestMethod, TestCategory("RhymeRuntime")]
    [DataRow("ru", "[Verse]\nПод крышей дремлет старый кот\nА за окном цветёт мой сад")]
    [DataRow("en", "[Verse]\nI watch the stars that shine so bright\nThe quiet garden sleeps till dawn")]
    public async Task RealCorpusAndCoreRun(string language, string lyrics)
    {
        if (Environment.GetEnvironmentVariable("LOPATA_RHYME_RUNTIME") != "1") { Assert.Inconclusive("Opt-in real model test"); return; }
        var settings = new StorageSettingsStore().LoadOrCreate();
        var model = MusicRhymePreferences.Choices(settings).Single(c => c.Candidate.Id == "core").Model!;
        Assert.IsNotNull(model);
        var request = Request(lyrics) with {
            ModelPath = model.Path, ModelsRoot = settings.Models.Locations.First().Path };
        var watch = System.Diagnostics.Stopwatch.StartNew(); var events = new List<object>();
        var work = new MusicRhymeWork(() => new MusicRhymeModel(new(new UserProfileStore(), new IpLocationService())));
        var result = await work.RunAsync(new() { Request = request, Original = request.Text }, s => s,
            new InlineProgress<RagBookProgress>(p => { events.Add(new { elapsed = watch.Elapsed.TotalSeconds, p.Stage, p.Done, p.Total }); }),
            new CancellationTokenSource(TimeSpan.FromMinutes(20)).Token);
        var root = Environment.GetEnvironmentVariable("LOPATA_MUSIC_UI_EVIDENCE") ?? Path.GetTempPath(); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "runtime-" + language + ".json"), JsonSerializer.Serialize(new { seconds = watch.Elapsed.TotalSeconds, result, events }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.IsTrue(result.Stage == "Ready" || result.Stage == "Partial" && result.Error is "Music.Rhyme.Unresolved" or "Music.Rhyme.Unchanged", result.Error);
        Assert.IsTrue(result.ModelReady); Assert.IsTrue(result.Reference.Length > 0);
        Assert.IsTrue(result.Suggestions.Length > 0); Assert.IsTrue(result.Prompt.Contains("stanza_context", StringComparison.Ordinal));
        if (language == "ru") Assert.AreNotEqual(result.Original, result.Program);
        if (result.Model == result.Original) Assert.AreEqual("Music.Rhyme.Unchanged", result.Error);
    }
    private static MusicRhymeRequest Request(string source) => new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), source, 0, source.Length, "core", null, "", "ru");
    private static IEnumerable<T> Desc<T>(DependencyObject root) where T : DependencyObject
    { if (root is T value) yield return value; foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) foreach (var item in Desc<T>(child)) yield return item; }
    private static void Theme(FrameworkElement window, bool dark)
    {
        window.Resources["TextPrimaryBrush"] = dark ? Brushes.White : Brushes.Black;
        window.Resources["TextSecondaryBrush"] = Brushes.Gray; window.Resources["PanelBrush"] = dark ? Brushes.MidnightBlue : Brushes.White;
        window.Resources["WindowBackgroundBrush"] = dark ? Brushes.Black : Brushes.WhiteSmoke; window.Resources["UiBodyFontSize"] = 14d;
        window.Resources["SecondaryButtonBackgroundBrush"] = dark ? Brushes.Black : Brushes.WhiteSmoke;
        window.Resources["LineBrush"] = Brushes.Gray; window.Resources["AccentBrush"] = Brushes.RoyalBlue;
    }
    private static void Modal(Window window, Action action)
    {
        window.Left = window.Top = -10000; window.ShowInTaskbar = false; Exception? failure = null;
        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => { try { action(); } catch (Exception e) { failure = e; } finally { window.Close(); } }));
        window.ShowDialog(); if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static void Snapshot(Window window, string name)
    {
        var root = Environment.GetEnvironmentVariable("LOPATA_MUSIC_UI_EVIDENCE"); if (root is null) return;
        Directory.CreateDirectory(root); window.UpdateLayout(); var visual = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var bounds = new Rect(0, 0, visual.ActualWidth, visual.ActualHeight);
            context.DrawRectangle((Brush)window.FindResource("WindowBackgroundBrush"), null, bounds);
            context.DrawRectangle(new VisualBrush(visual), null, bounds);
        }
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(root, name + ".png")); encoder.Save(file);
    }
}
