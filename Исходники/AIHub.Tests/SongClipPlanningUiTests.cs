using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Services;
using AIHub.Services.SongClip;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class SongClipPlanningUiTests
{
    [TestMethod]
    [DataRow("ru")]
    [DataRow("en")]
    public Task OptionalDirectionShowsAnExampleWithoutSavingItOrCreatingEmptyProjects(string language) => OnUi(() =>
    {
        using var fixture = new Fixture(language);
        var control = fixture.Control();
        var prompt = Find<TextBox>(control, "Prompt");
        var example = ScenarioNavigationTests.LogicalDescendants(control).OfType<ExampleTextInput>()
            .Single(item => ReferenceEquals(item.Input, prompt));
        Assert.AreEqual("", prompt.Text);
        Assert.IsTrue(example.ExampleVisible);
        Assert.AreEqual(fixture.Localize.T("SongClip.PromptExample"), AutomationProperties.GetHelpText(prompt));
        Assert.AreNotEqual("SongClip.PromptExample", AutomationProperties.GetHelpText(prompt));
        Assert.IsTrue(control.Checkpoint());
        Assert.AreEqual(0, fixture.Store.List().Count);

        prompt.Text = "Тёплый вечер, мягкий свет и спокойная камера.";
        Assert.IsFalse(example.ExampleVisible);
        Assert.IsTrue(control.Checkpoint());
        var saved = fixture.Store.List().Single();
        Assert.AreEqual(prompt.Text, saved.Settings.Prompt);
        Find<Button>(control, "New").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.AreEqual("", prompt.Text);
        Assert.IsTrue(example.ExampleVisible);
        Assert.IsTrue(control.Checkpoint());
        Assert.AreEqual(1, fixture.Store.List().Count, "An untouched new workspace is not another project.");
        var savedText = File.ReadAllText(Path.Combine(fixture.Store.Folder(saved.Id), "project.json"));
        Assert.DoesNotContain(AutomationProperties.GetHelpText(prompt), savedText,
            "The example must remain presentation only.");
    });

    [TestMethod]
    [DataRow("glitchframe", "")]
    [DataRow("h3_music_context", "")]
    [DataRow("glitchframe", "Сказочный город и добрый юмор.")]
    [DataRow("h3_music_context", "Сказочный город и добрый юмор.")]
    public Task NewAttemptsEnablePlanningAndAllowAnEmptyOrLocalLanguageDirection(string model, string direction) => OnUi(() =>
    {
        using var fixture = new Fixture("ru");
        var project = fixture.Project(model, direction);
        var control = fixture.Control(project);
        var attempt = control.SelectStartAttempt();
        Assert.AreEqual(1, attempt.PlanningVersion);
        Assert.AreEqual(direction, attempt.Settings.Prompt);
        Assert.AreEqual(project.Lyrics, attempt.Lyrics);
        Assert.AreEqual(project.Timings, attempt.Timings);
        Assert.IsTrue(control.TryValidateStartAttempt(attempt));
        Assert.AreEqual("", fixture.Store.Load(project.Id).PendingAttemptId);
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Store.Folder(project.Id), "attempts")),
            "Selecting and validating input must not begin inference or create a half-started attempt.");
    });

    [TestMethod]
    public Task NewAttemptWithoutReferencesRetainsItsPendingIdentityAfterProjectRoundtrip() => OnUi(() =>
    {
        using var fixture = new Fixture("ru");
        var project = fixture.Project("h3_music_context", "");
        var control = fixture.Control(project);
        var attempt = control.SelectStartAttempt();
        Assert.IsNotNull(attempt.References); Assert.HasCount(0, attempt.References);
        Assert.IsTrue(control.Checkpoint());
        var stored = fixture.Store.Load(project.Id);
        Assert.IsNotNull(stored.References); Assert.HasCount(0, stored.References);
        stored.PendingAttemptId = attempt.Id; fixture.Store.Save(stored); fixture.Store.EnsureAttemptSnapshot(attempt);
        var reopened = fixture.Control(stored);
        var resumed = reopened.SelectStartAttempt();
        Assert.AreEqual(attempt.Id, resumed.Id);
        Assert.AreEqual(JsonSerializer.Serialize(attempt), JsonSerializer.Serialize(resumed));
    });

    [TestMethod]
    [DataRow("glitchframe", false)]
    [DataRow("glitchframe", true)]
    [DataRow("h3_music_context", false)]
    [DataRow("h3_music_context", true)]
    public Task MatchingLegacyAttemptAndExplicitRestorationKeepTheirOriginalIdentity(string model, bool generic) => OnUi(() =>
    {
        using var fixture = new Fixture("en");
        var direction = generic ? new SongClipSettings().Prompt : "A cat exploring a sunlit workshop.";
        var project = fixture.Project(model, direction);
        var legacy = fixture.Legacy(project);
        project.PendingAttemptId = legacy.Id; fixture.Store.Save(project);
        fixture.Store.EnsureAttemptSnapshot(legacy);
        var path = fixture.Store.Resolve(project.Id, "attempts/" + legacy.Id + "/snapshot.json");
        var before = File.ReadAllBytes(path);
        var control = fixture.Control(project);
        var selected = control.SelectStartAttempt();
        Assert.AreEqual(0, selected.PlanningVersion);
        Assert.AreEqual(legacy.Id, selected.Id);
        Assert.AreEqual(direction, selected.Settings.Prompt);
        Assert.AreEqual(JsonSerializer.Serialize(legacy), JsonSerializer.Serialize(selected));
        Assert.DoesNotContain("PlanningVersion", JsonSerializer.Serialize(selected));
        Assert.AreSame(legacy, control.SelectStartAttempt(legacy), "Restoration must return the supplied immutable input unchanged.");
        CollectionAssert.AreEqual(before, File.ReadAllBytes(path));

        Find<TextBox>(control, "Prompt").Text = "Новая художественная идея.";
        Assert.IsTrue(control.Checkpoint());
        var changed = control.SelectStartAttempt();
        Assert.AreEqual(1, changed.PlanningVersion);
        Assert.AreNotEqual(legacy.Id, changed.Id, "Editing the direction creates new work rather than rewriting an old request.");
        CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
    });

    [TestMethod]
    [DataRow("glitchframe")]
    [DataRow("h3_music_context")]
    public Task SongImportPreservesSameProjectContinuationAndClearsOnlyANewProjectsDirection(string model) => OnUi(() =>
    {
        using var fixture = new Fixture("en");
        var current = fixture.Project(model, "Keep the existing creative direction.");
        var legacy = fixture.Legacy(current);
        current.PendingAttemptId = legacy.Id; fixture.Store.Save(current); fixture.Store.EnsureAttemptSnapshot(legacy);
        var control = fixture.Control(current);
        var sameSong = fixture.Store.Load(current.Id);
        control.AdoptImportedSong(sameSong);
        Assert.AreEqual(legacy.Id, control.SelectStartAttempt().Id);
        Assert.AreEqual(0, control.SelectStartAttempt().PlanningVersion);
        Assert.AreEqual(current.Settings.Prompt, sameSong.Settings.Prompt);
        Assert.AreEqual(legacy.Id, sameSong.PendingAttemptId);

        var differentSong = new SongClipProject
        {
            Model = model, Song = "attachments/new-song.wav", Name = "Different song",
            Settings = current.Settings with { Prompt = new SongClipSettings().Prompt }
        };
        control.AdoptImportedSong(differentSong);
        Assert.AreEqual("", differentSong.Settings.Prompt);
        Assert.AreEqual("", fixture.Store.Load(differentSong.Id).Settings.Prompt);
        Assert.AreEqual(1, control.SelectStartAttempt().PlanningVersion);
        Assert.AreEqual("Keep the existing creative direction.", current.Settings.Prompt);
        Assert.AreEqual(legacy.Id, fixture.Store.Load(current.Id).PendingAttemptId);
    });

    [TestMethod]
    [DataRow("ru", "core", "Qwen3-8B")]
    [DataRow("en", "core", "Qwen3-8B")]
    [DataRow("ru", "executor", "Qwen3.6-27B")]
    [DataRow("en", "executor", "Qwen3.6-27B")]
    public Task PlanningProgressNamesItsBatchAndActualAssistant(string language, string model, string assistant) => OnUi(() =>
    {
        using var fixture = new Fixture(language);
        var control = fixture.Control();
        var format = typeof(SongClipControl).GetMethod("FormatEngineProgress", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var message = JsonSerializer.Serialize(new { kind = "planner", stage = "SongClip.PlanningBatch", current = 2, total = 4, model });
        var displayed = (string)format.Invoke(control, [message])!;
        Assert.AreEqual(string.Format(System.Globalization.CultureInfo.CurrentCulture,
            fixture.Localize.T("SongClip.PlanningBatch"), 2, 4, assistant), displayed);
        Assert.Contains(assistant, displayed);
        Assert.DoesNotContain("SongClip.", displayed);
        Assert.DoesNotContain("\"kind\"", displayed);
    });

    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject =>
        ScenarioNavigationTests.LogicalDescendants(root).OfType<T>()
            .Single(element => AutomationProperties.GetAutomationId(element) == "SongClip." + id);

    private sealed class Fixture : IDisposable
    {
        private readonly VideoPreparationTests.TestFiles _files = new();
        private readonly BackgroundOperationController? _previous = ApplicationBackgroundOperations.Current;
        internal SongClipStore Store { get; }
        internal LocalizationService Localize { get; } = new();
        internal Fixture(string language)
        {
            Store = new(_files.File("projects"));
            Localize.Load(language);
            ApplicationBackgroundOperations.Current = null;
        }
        internal SongClipControl Control(SongClipProject? project = null)
        {
            var control = new SongClipControl(Localize.T, Store);
            if (project is not null) control.OpenProject(project.Id);
            return control;
        }
        internal SongClipProject Project(string model, string prompt)
        {
            var project = new SongClipProject
            {
                Model = model, Song = "attachments/song.wav", Lyrics = "Words from the complete song.",
                Timings = "[00:01.00]Words from the complete song.", Settings = new SongClipSettings { Prompt = prompt }
            };
            Store.Save(project); return project;
        }
        internal SongClipAttempt Legacy(SongClipProject project) => new(Guid.NewGuid().ToString("N"), project.Id,
            project.Song, project.Reference, project.Name, project.Lyrics, project.Timings,
            project.Settings with { Seed = 123, Motion = project.Settings.Motion with { } }, _files.File("models"), 8)
        { Model = project.Model };
        public void Dispose()
        {
            ApplicationBackgroundOperations.Current = _previous;
            _files.Dispose();
        }
    }

    private static Task OnUi(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); done.SetResult(); }
            catch (Exception error) { done.SetException(error); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task.WaitAsync(TimeSpan.FromSeconds(35));
    }
}
