using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicOutputFolderTests
{
    [TestMethod]
    public void SelectingOrCancelingNeverSubmitsAndNonemptyPathsKeepTheirExistingErrorRoute()
    {
        var chosen = 0; var notices = 0;
        foreach (var empty in new[] { "", "   ", "\t\r\n" })
        {
            Assert.IsFalse(MusicOutputFolderPrompt.Check(empty, () => { notices++; return false; }, () => chosen++));
            Assert.IsFalse(MusicOutputFolderPrompt.Check(empty, () => { notices++; return true; }, () => chosen++));
        }
        Assert.AreEqual(6, notices); Assert.AreEqual(3, chosen);
        Assert.IsTrue(MusicOutputFolderPrompt.Check(@"Z:\disconnected\music", () => throw new Exception("Must not claim missing"), () => chosen++));
        using var files = new MusicProjectTests.Files();
        Assert.Throws<ArgumentException>(() => new MusicGenerationJobs(files.Root).Create(files.Root, " ", "", 1, 30, "", ""));
    }

    [TestMethod, DataRow("ru", true), DataRow("ru", false), DataRow("en", true), DataRow("en", false)]
    public Task LocalizedDialogHasKeyboardActionsAndClosesWithoutChoice(string language, bool dark) => ScenarioNavigationTests.Sta(() =>
    {
        var l = new LocalizationService(); l.Load(language);
        var dialog = new MusicOutputFolderPrompt(null, l.T) { ShowInTaskbar = false, Left = -10000, Top = -10000,
            WindowStartupLocation = WindowStartupLocation.Manual };
        dialog.Resources["WindowBackgroundBrush"] = dark ? Brushes.Black : Brushes.White;
        dialog.Resources["TextPrimaryBrush"] = dark ? Brushes.White : Brushes.Black;
        var buttons = Buttons(dialog).ToArray();
        var choose = buttons.Single(b => AutomationProperties.GetAutomationId(b) == "Music.OutputFolder.Choose");
        var cancel = buttons.Single(b => AutomationProperties.GetAutomationId(b) == "Music.OutputFolder.Cancel");
        Assert.IsTrue(choose.IsDefault); Assert.IsTrue(cancel.IsCancel);
        Assert.AreEqual(l.T("Music.OutputFolder.Title"), dialog.Title);
        Assert.IsFalse(dialog.Title.StartsWith("Music.", StringComparison.Ordinal));
        dialog.Loaded += (_, _) => cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.AreEqual(false, dialog.ShowDialog());
    });

    [TestMethod]
    public Task RestoredEmptyProjectBlocksStartRepeatAndReentryWithoutCreatingJobs() => ScenarioNavigationTests.Sta(() =>
    {
        using var files = new MusicProjectTests.Files();
        var store = new MusicProjects(Path.Combine(files.Root, "projects"));
        var jobRoot = Path.Combine(files.Root, "jobs"); var jobs = new MusicGenerationJobs(jobRoot);
        using var view = new MusicWorkspaceControl(store, jobs, new MusicOutputPreferences(Path.Combine(files.Root, "output.json")));
        view.Editor.Lyrics = "Не терять текст"; view.Projects.Fix(); var id = view.Projects.Current.Id;
        var saves = 0; view.ConfigureOutput(files.Root, _ => saves++);
        view.Projects.Open(id); // Deliberately restore an empty project destination over the global folder.
        Assert.AreEqual("", view.Tracks.OutputFolder);
        typeof(MusicGenerationSession).GetField("_ready", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view.Session, true);
        var prompts = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        {
            var prompt = PresentationSource.CurrentSources.OfType<HwndSource>().Select(s => s.RootVisual)
                .OfType<MusicOutputFolderPrompt>().FirstOrDefault();
            if (prompt is null) return;
            prompts++;
            Invoke(view.Session, "StartPauseAsync").GetAwaiter().GetResult(); // Reentrant click is ignored while the prompt is open.
            Buttons(prompt).Single(b => AutomationProperties.GetAutomationId(b) == "Music.OutputFolder.Cancel")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        };
        timer.Start();
        try
        {
            Invoke(view.Session, "StartPauseAsync").GetAwaiter().GetResult();
            var track = new MusicTrack(Path.Combine(files.Root, "source.wav"), "Original", TimeSpan.Zero, DateTime.UtcNow)
                { JobId = Guid.NewGuid().ToString("N") };
            Invoke(view.Session, "RepeatAsync", track).GetAwaiter().GetResult();
        }
        finally { timer.Stop(); }
        Assert.AreEqual(2, prompts); Assert.AreEqual(0, saves);
        Assert.AreEqual("Не терять текст", view.Editor.Lyrics); Assert.AreEqual(id, view.Projects.Current.Id);
        Assert.HasCount(0, view.Projects.Current.Steps);
        Assert.IsFalse(Directory.Exists(jobRoot) && Directory.EnumerateDirectories(jobRoot).Any());
    });

    private static Task Invoke(MusicGenerationSession session, string method, params object[] args) =>
        (Task)typeof(MusicGenerationSession).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, args)!;
    private static IEnumerable<Button> Buttons(DependencyObject root)
    {
        if (root is Button button) yield return button;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var buttonChild in Buttons(child)) yield return buttonChild;
    }
}
