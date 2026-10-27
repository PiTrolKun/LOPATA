using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Controls;
using AIHub.Services;
using AIHub.Services.VideoPreparation;
using Button = System.Windows.Controls.Button;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoPreparationUiTests
{
    [TestMethod]
    [DataRow("ru", "story", 1100d, true)]
    [DataRow("en", "imagery", 620d, false)]
    public Task ManualConceptIsSavedBeforeDevelopmentAndSurvivesNavigation(string language, string form, double width, bool dark) => ScenarioNavigationTests.Sta(() =>
    {
        using var files = new VideoPreparationTests.TestFiles();
        var session = new VideoPreparationSession(files.File("recovery"));
        session.Project.Form = form; session.Project.Title = "Project title is not the concept";
        session.Save(files.File("concept.lopataAIVid"));
        var localizer = new LocalizationService(); localizer.Load(language);
        var shell = new VideoPreparationControl(localizer.T, session);
        var host = ScenarioNavigationTests.Create(language, 1.25, dark); host.Content = shell;
        Assert.IsTrue(shell.Next()); Assert.AreEqual("idea", session.Project.CurrentStep);
        var input = Idea(shell);
        Assert.AreEqual("", input.Input.Text); Assert.IsTrue(input.ExampleVisible);
        Assert.IsTrue(input.Input.AcceptsReturn); Assert.AreEqual(TextWrapping.Wrap, input.Input.TextWrapping);
        Assert.IsFalse(shell.Next()); Assert.AreEqual("idea", session.Project.CurrentStep);
        input.Input.Text = " \r\n\t";
        Assert.IsFalse(shell.Next()); Assert.AreEqual(" \r\n\t", session.Project.IdeaText);
        input.Input.Text = "  Мирон и LOPATA.\r\nБез монстров. Don't change «Лопата».  ";
        var text = input.Input.Text;
        Assert.IsFalse(input.ExampleVisible); Assert.AreEqual(text, session.Project.IdeaText);
        Assert.IsTrue(shell.Checkpoint());
        Assert.AreEqual(text, VideoPreparationArchive.Load(session.ArchivePath!, files.File("opened")).IdeaText);
        var otherLanguage = new LocalizationService(); otherLanguage.Load(language == "ru" ? "en" : "ru");
        shell.Configure(otherLanguage.T); Assert.AreEqual(text, Idea(shell).Input.Text);
        shell.Configure(localizer.T);
        host.Measure(new Size(width, 800)); host.Arrange(new Rect(0, 0, width, 800)); host.UpdateLayout();
        Assert.IsTrue(Idea(shell).ActualWidth > 100); Assert.IsTrue(Idea(shell).ActualWidth <= width);
        Assert.IsFalse(Find<TextBlock>(shell).Any(t => t.Text.StartsWith("VideoPrep.", StringComparison.Ordinal)));
        Assert.IsTrue(shell.Next()); Assert.AreEqual("translation", session.Project.CurrentStep);
        Assert.AreEqual(Visibility.Visible, Find<Button>(shell).Single(b => AutomationProperties.GetAutomationId(b) == "VideoPrep.Next").Visibility);
        Assert.AreEqual(text, VideoPreparationArchive.Load(session.ArchivePath!, files.File("opened2")).IdeaText);
        Assert.IsTrue(shell.BackStepIfNeeded()); Assert.AreEqual("idea", session.Project.CurrentStep);
        Assert.AreEqual(text, Idea(shell).Input.Text);
        Assert.IsTrue(shell.BackStepIfNeeded()); Assert.AreEqual("form", session.Project.CurrentStep);
        var nextForm = form == "story" ? "imagery" : "story";
        Find<RadioButton>(shell).Single(c => AutomationProperties.GetAutomationId(c) == "VideoPrep.Form." + nextForm).IsChecked = true;
        Assert.IsTrue(shell.Next()); Assert.AreEqual(text, Idea(shell).Input.Text);
        Idea(shell).Input.Text = ""; Assert.IsTrue(Idea(shell).ExampleVisible);
        Assert.IsTrue(shell.Checkpoint());
        Assert.AreEqual("", VideoPreparationArchive.Load(session.ArchivePath!, files.File("opened3")).IdeaText);
    });

    [TestMethod]
    public Task ConceptRecoveryAndSaveFailureRetainTextAndCurrentStep() => ScenarioNavigationTests.Sta(() =>
    {
        using var files = new VideoPreparationTests.TestFiles();
        var localizer = new LocalizationService(); localizer.Load("ru");
        var session = new VideoPreparationSession(files.File("recovery"));
        session.Project.Form = "story"; session.Project.CurrentStep = "idea";
        var shell = new VideoPreparationControl(localizer.T, session);
        Idea(shell).Input.Text = "Мой замысел\r\nLOPATA";
        Assert.IsTrue(shell.Checkpoint()); Assert.IsNull(session.ArchivePath);
        Assert.IsFalse(shell.Next()); Assert.IsTrue(shell.LocationErrorVisible);
        var restored = new VideoPreparationSession(files.File("recovery2")); restored.Load(session.RecoveryPath, recovery: true);
        Assert.AreEqual(session.Project.IdeaText, restored.Project.IdeaText);
        session.Save(files.File("chosen.lopataAIVid"));
        using (var locked = new FileStream(session.ArchivePath!, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Idea(shell).Input.Text += "\r\nНе потерять";
            Assert.IsFalse(shell.Next()); Assert.AreEqual("idea", session.Project.CurrentStep);
            Assert.IsTrue(shell.BackStepIfNeeded()); Assert.AreEqual("idea", session.Project.CurrentStep);
            StringAssert.EndsWith(Idea(shell).Input.Text, "Не потерять");
        }
        Assert.IsTrue(shell.Next()); Assert.AreEqual("translation", session.Project.CurrentStep);
    });

    private static ExampleTextInput Idea(DependencyObject shell) => Find<ExampleTextInput>(shell)
        .Single(t => AutomationProperties.GetAutomationId(t.Input) == "VideoPrep.Idea");

    [TestMethod]
    public Task MissingPathSignalsWithoutLosingInputThenSavesAndReturns() => ScenarioNavigationTests.Sta(() =>
    {
        using var files = new VideoPreparationTests.TestFiles();
        var localizer = new LocalizationService(); localizer.Load("ru");
        var session = new VideoPreparationSession(files.File("recovery"));
        var control = new VideoPreparationControl(localizer.T, session);
        var title = Find<ExampleTextInput>(control).Single(t => AutomationProperties.GetAutomationId(t.Input) == "VideoPrep.Title");
        Assert.AreEqual("", title.Input.Text); Assert.IsTrue(title.ExampleVisible);
        title.Input.Text = "Тестовый клип"; Assert.IsFalse(title.ExampleVisible);
        title.Input.Text = ""; Assert.IsTrue(title.ExampleVisible);
        title.Input.Text = "Тестовый клип";
        Find<RadioButton>(control).Single(c => AutomationProperties.GetAutomationId(c) == "VideoPrep.Form.story").IsChecked = true;
        Assert.IsFalse(control.Next()); Assert.IsTrue(control.LocationErrorVisible);
        Assert.AreEqual("Тестовый клип", session.Project.Title); Assert.AreEqual("form", session.Project.CurrentStep);
        Assert.IsNull(session.ArchivePath);
        session.Save(files.File("chosen.lopataAIVid"));
        Assert.IsTrue(control.Next()); Assert.IsFalse(control.LocationErrorVisible);
        Assert.AreEqual("idea", session.Project.CurrentStep);
        Assert.AreEqual("idea", VideoPreparationArchive.Load(session.ArchivePath!, files.File("opened")).CurrentStep);
        Assert.IsTrue(control.BackStepIfNeeded());
        Assert.AreEqual("Тестовый клип", session.Project.Title); Assert.AreEqual("story", session.Project.Form);
    });

    [TestMethod]
    [DataRow("ru", 1150d, true)]
    [DataRow("en", 620d, false)]
    public Task LocalizedShellSurvivesResizeAndNavigation(string language, double width, bool dark) => ScenarioNavigationTests.Sta(() =>
    {
        var navigation = ScenarioNavigationTests.Create(language, 1.25, dark);
        var requests = new List<string>(); navigation.ScenarioRequested += requests.Add;
        navigation.SelectDirection(ScenarioNavigationCatalog.Creation); navigation.OpenScenario(ScenarioNavigationCatalog.VideoGeneration);
        Assert.IsTrue(navigation.IsVideoPreparation); Assert.IsNotNull(navigation.VideoPreparation);
        var shell = navigation.VideoPreparation;
        Assert.AreEqual(0, requests.Count); Assert.IsFalse(ScenarioNavigationCatalog.Get(ScenarioNavigationCatalog.VideoGeneration).IsPlaceholder);
        navigation.Measure(new Size(width, 760)); navigation.Arrange(new Rect(0, 0, width, 760)); navigation.UpdateLayout();
        Assert.IsFalse(Find<TextBlock>(shell).Any(t => t.Text.StartsWith("VideoPrep.", StringComparison.Ordinal)));
        var next = Find<Button>(shell).Single(b => AutomationProperties.GetAutomationId(b) == "VideoPrep.Next");
        Assert.AreEqual(System.Windows.Media.Colors.White, ((System.Windows.Media.SolidColorBrush)((TextBlock)next.Content).Foreground).Color);
        Assert.IsNotNull(Find<Button>(shell).Single(b => AutomationProperties.GetAutomationId(b) == "VideoPrep.Save").Style);
        Assert.IsFalse(next.IsEnabled);
        Assert.IsTrue(navigation.GoBack()); Assert.IsFalse(navigation.IsVideoPreparation);
        navigation.OpenScenario(ScenarioNavigationCatalog.VideoGeneration); Assert.AreSame(shell, navigation.VideoPreparation);
        var otherLanguage = new LocalizationService(); otherLanguage.Load(language == "ru" ? "en" : "ru");
        navigation.Configure(otherLanguage.T); Assert.IsTrue(navigation.IsVideoPreparation); Assert.AreSame(shell, navigation.VideoPreparation);
        Assert.IsFalse(Find<TextBlock>(shell).Any(t => t.Text.StartsWith("VideoPrep.", StringComparison.Ordinal)));
    });

    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject => ScenarioNavigationTests.LogicalDescendants(root).OfType<T>();
}
