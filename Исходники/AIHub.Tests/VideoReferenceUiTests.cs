using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Services;
using AIHub.Services.VideoPreparation;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoReferenceUiTests
{
    private static readonly Lazy<Dispatcher> Ui = new(() =>
    {
        var ready = new TaskCompletionSource<Dispatcher>();
        var thread = new Thread(() => { var d = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(d)); ready.SetResult(d); Dispatcher.Run(); }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return ready.Task.GetAwaiter().GetResult();
    });
    private static Task OnUi(Func<Task> action) => Ui.Value.InvokeAsync(action).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(40));
    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject => ScenarioNavigationTests.LogicalDescendants(root)
        .OfType<T>().Single(e => AutomationProperties.GetAutomationId(e) == id);
    private static void Click(DependencyObject root, string id) => Find<Button>(root, id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static VideoPreparationControl Control(VideoPreparationTests.TestFiles files, string language, out LocalizationService l)
    {
        var p = VideoWorldTests.Root(); p.World!.Cards[0].Name = "Workshop"; p.World.Cards[0].Text.IdeaText = "Soft blue light.";
        VideoWorld.Capture(p); VideoTranslationHistory.UseOriginal(p.World.Working); p.CurrentStep = "references";
        var path = files.File("project.lopataAIVid"); VideoPreparationArchive.Save(p, path);
        var session = new VideoPreparationSession(files.File("recovery")); session.Load(path);
        l = new LocalizationService(); l.Load(language); return new(l.T, session);
    }
    [TestMethod]
    [DataRow("ru")]
    [DataRow("en")]
    public Task ImportPreviewExplicitBindingAndUnlinkPreserveOriginalAndTranslations(string language) => OnUi(async () =>
    {
        using var files = new VideoPreparationTests.TestFiles(); var control = Control(files, language, out var l);
        var p = control.Session.Project; var original = p.IdeaText; var english = p.World!.Working.EnglishText;
        Click(control, "VideoPrep.RefHaveFiles"); var source = VideoReferenceTests.Image(files);
        Assert.AreEqual(1, await control.ImportReferenceFilesAsync([source]));
        Assert.IsNotNull(Find<System.Windows.Controls.Image>(control, "VideoPrep.RefPreview").Source);
        Assert.HasCount(0, p.References!.Bindings);
        var target = Find<ComboBox>(control, "VideoPrep.RefTarget");
        Assert.IsTrue(target.Items.Cast<object>().Any(i => i.ToString() == "Workshop"));
        target.SelectedValue = p.World.Cards[0].Id;
        var notes = Find<TextBox>(control, "VideoPrep.RefNotes"); Assert.IsTrue(SpellCheck.GetIsEnabled(notes));
        Assert.AreEqual(language == "en" ? "en-us" : "ru-ru", notes.Language.IetfLanguageTag.ToLowerInvariant());
        notes.Text = "Keep the colour."; Click(control, "VideoPrep.RefBind");
        var binding = p.References.Bindings.Single(); Assert.AreEqual(p.World.Cards[0].Id, binding.CardId);
        var image = p.References.Images.Single(); Assert.IsTrue(control.Save());
        var reload = VideoPreparationArchive.Load(control.Session.ArchivePath!, files.File("opened"));
        Assert.AreEqual(image.Id, reload.References!.Images.Single().Id); Assert.AreEqual(binding.Id, reload.References.Bindings.Single().Id);
        Click(control, "VideoPrep.RefUnlink." + binding.Id);
        Assert.HasCount(0, p.References.Bindings); Assert.IsTrue(System.IO.File.Exists(VideoReferences.PathFor(p, image)));
        Assert.AreEqual(original, p.IdeaText); Assert.AreEqual(english, p.World.Working.EnglishText);
        LiterarySpellChecking.ReleaseTree(control);
    });
    [TestMethod]
    public Task ChoiceSkipAndReturnKeepFilesAndCreateBranchOffersBothModes() => OnUi(async () =>
    {
        using var files = new VideoPreparationTests.TestFiles(); var control = Control(files, "ru", out _);
        Click(control, "VideoPrep.RefNeedCreate"); Assert.IsFalse(control.Next());
        StringAssert.Contains(Find<TextBlock>(control, "VideoPrep.Status").Text, "Заполните оба текста");
        Assert.IsNotNull(Find<Button>(control, "VideoPrep.RefPlanMode.assisted"));
        Assert.IsNotNull(Find<Button>(control, "VideoPrep.RefPlanMode.manual"));
        await control.ImportReferenceFilesAsync([VideoReferenceTests.Image(files)]);
        var id = control.Session.Project.References!.Images.Single().Id;
        Click(control, "VideoPrep.RefSkip"); Assert.IsTrue(await control.NextAsync());
        Assert.AreEqual("shots", control.Session.Project.CurrentStep);
        Assert.IsTrue(control.BackStepIfNeeded()); Assert.AreEqual("references", control.Session.Project.CurrentStep);
        Click(control, "VideoPrep.RefHaveFiles"); Assert.AreEqual(id, control.Session.Project.References.Images.Single().Id);
        Assert.IsNotNull(Find<System.Windows.Controls.Image>(control, "VideoPrep.RefPreview").Source);
        LiterarySpellChecking.ReleaseTree(control);
    });
    [TestMethod]
    public Task PartialImportKeepsGoodImageAndCanceledImportIsSafe() => OnUi(async () =>
    {
        using var files = new VideoPreparationTests.TestFiles(); var control = Control(files, "en", out _);
        var bad = files.File("bad.png"); System.IO.File.WriteAllText(bad, "broken");
        Assert.AreEqual(1, await control.ImportReferenceFilesAsync([bad, VideoReferenceTests.Image(files)]));
        Assert.HasCount(1, control.Session.Project.References!.Images);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.AreEqual(0, await control.ImportReferenceFilesAsync([VideoReferenceTests.Image(files, "cancel.png")], cancel.Token));
        Assert.HasCount(1, control.Session.Project.References.Images);
        Assert.AreEqual(1, VideoPreparationArchive.Load(control.Session.RecoveryPath, files.File("recovered")).References!.Images.Count);
        LiterarySpellChecking.ReleaseTree(control);
    });
    [TestMethod]
    public Task NextWarnsAboutUnlinkedImagesAndCancelPreservesCurrentStep() => OnUi(async () =>
    {
        using var files = new VideoPreparationTests.TestFiles(); var control = Control(files, "en", out _);
        await control.ImportReferenceFilesAsync([VideoReferenceTests.Image(files)]);
        var owner = new Window { Content = control }; owner.Show();
        _ = owner.Dispatcher.InvokeAsync(() =>
        {
            var dialog = owner.OwnedWindows.OfType<VideoPreparationDecisionWindow>().Single();
            Click(dialog, "VideoPrep.Decision.cancel");
        }, DispatcherPriority.Background);
        Assert.IsFalse(await control.NextAsync()); Assert.AreEqual("references", control.Session.Project.CurrentStep);
        Assert.HasCount(1, control.Session.Project.References!.Images); owner.Close();
    });
}
