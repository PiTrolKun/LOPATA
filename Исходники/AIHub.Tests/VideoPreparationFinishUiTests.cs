using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Services;
using AIHub.Services.VideoPreparation;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoPreparationFinishUiTests
{
    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject => ScenarioNavigationTests.LogicalDescendants(root)
        .OfType<T>().Single(e => AutomationProperties.GetAutomationId(e) == id);
    private static readonly Lazy<Dispatcher> Ui = new(() =>
    {
        var ready = new TaskCompletionSource<Dispatcher>(); var thread = new Thread(() =>
        { var d = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(d)); ready.SetResult(d); Dispatcher.Run(); }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return ready.Task.GetAwaiter().GetResult();
    });
    private static Task OnUi(Func<Task> work) => Ui.Value.InvokeAsync(work).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(40));
    private static VideoPreparationControl Control(VideoPreparationTests.TestFiles f, string language = "ru")
    {
        var p = VideoPreparationFinishTests.ReadyProject(); VideoPreparationArchive.Save(p, f.File("p.lopataAIVid"));
        var s = new VideoPreparationSession(f.File("recovery")); s.Load(f.File("p.lopataAIVid"));
        var l = new LocalizationService(); l.Load(language); return new(l.T, s);
    }
    [TestMethod]
    public Task StoryboardManualMenuOrderClearAndEditWorkWithHistory() => OnUi(() =>
    {
        using var files = new VideoPreparationTests.TestFiles(); var control = Control(files);
        var p = control.Session.Project; var b = p.Storyboard!; var frame = b.Frames[0];
        frame.ImageId = Guid.NewGuid().ToString("N");
        var card = Find<Button>(control, "VideoPrep.Board.Frame." + frame.Id);
        Assert.IsNotNull(card.ContextMenu); Assert.HasCount(4, card.ContextMenu.Items);
        var clear = card.ContextMenu.Items.OfType<System.Windows.Controls.MenuItem>().Single(m =>
            AutomationProperties.GetAutomationId(m).Contains(".BoardClearImage.", StringComparison.Ordinal));
        clear.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
        Assert.IsEmpty(frame.ImageId);
        b.Frames.Reverse(); b.Frames.Single(f => f.Id == frame.Id).Seconds = 11;
        Find<Button>(control, "VideoPrep.BoardPlanOrder").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.AreEqual(frame.Id, b.Frames[0].Id); Assert.AreEqual(11, b.Frames[0].Seconds);
        Find<Button>(control, "VideoPrep.BoardUndo").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.AreNotEqual(frame.Id, b.Frames[0].Id);
        Find<Button>(control, "VideoPrep.BoardRedo").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.AreEqual(frame.Id, b.Frames[0].Id);
        Find<Button>(control, "VideoPrep.BoardEditPlan").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.AreEqual("shots", p.CurrentStep); Assert.AreEqual(frame.ShotId, p.Shots!.SelectedShotId);
        return Task.CompletedTask;
    });
    [TestMethod, DataRow("ru", true), DataRow("en", false)]
    public Task FinishUsesHorizontalActionsAndPopupThenRoutesToSelectedModel(string language, bool dark) => OnUi(async () =>
    {
        using var f = new VideoPreparationTests.TestFiles(); var control = Control(f, language);
        control.Resources["TextPrimaryBrush"] = new SolidColorBrush(dark ? Colors.White : Colors.Black);
        var owner = new Window { Content = control, Width = 1220, Height = 880 }; owner.Show();
        owner.Resources["TextPrimaryBrush"] = new SolidColorBrush(dark ? Colors.White : Colors.Black);
        owner.Resources["TextSecondaryBrush"] = new SolidColorBrush(dark ? Colors.LightGray : Colors.DimGray);
        owner.Resources["PanelBrush"] = new SolidColorBrush(dark ? Color.FromRgb(24, 33, 49) : Colors.WhiteSmoke);
        owner.Resources["WindowBackgroundBrush"] = new SolidColorBrush(dark ? Color.FromRgb(16, 24, 39) : Colors.White);
        owner.Resources["LineBrush"] = new SolidColorBrush(Colors.Gray);
        owner.Resources["SecondaryButtonBackgroundBrush"] = owner.Resources["PanelBrush"];
        owner.Resources["AccentBrush"] = new SolidColorBrush(Colors.RoyalBlue);
        owner.Resources["UiBodyFontSize"] = 14d; owner.Resources["UiSectionFontSize"] = 18d;
        try
        {
            control.UpdateLayout();
            var first = Find<Button>(control, "VideoPrep.Board.Frame." + control.Session.Project.Storyboard!.Frames[0].Id);
            Assert.IsLessThan(160, first.TranslatePoint(new Point(), Find<TextBox>(control, "VideoPrep.Board.Duration")).Y);
            var duration = Find<TextBox>(control, "VideoPrep.Board.Duration"); duration.Text = "no"; Assert.IsFalse(control.Next());
            duration.Text = "4,25"; Assert.IsTrue(control.Next()); Assert.AreEqual("package", control.Session.Project.CurrentStep);
            Assert.IsTrue(control.Next()); Assert.AreEqual("finish", control.Session.Project.CurrentStep); Assert.IsFalse(VideoGeneratorPlan.CanResume(control.Session.Project));
            var export = Find<Button>(control, "VideoPrep.FinishExport"); var choose = Find<Button>(control, "VideoPrep.FinishChooseModel");
            Assert.AreSame(export.Parent, choose.Parent); Assert.IsInstanceOfType<WrapPanel>(choose.Parent);
            Assert.AreEqual(Visibility.Visible, Find<Button>(control, "VideoPrep.Next").Visibility);
            var popupHeight = 0d;
            _ = owner.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                var picker = owner.OwnedWindows.OfType<VideoGeneratorChoiceWindow>().Single();
                var list = Find<System.Windows.Controls.ListBox>(picker, "VideoPrep.ModelPicker.List");
                CollectionAssert.AreEqual(new[] { "minimax-h3", VideoH3LiteCatalog.ModelId }, list.Items.Cast<VideoGeneratorCandidate>().Select(c => c.Id).ToArray());
                list.SelectedItem = list.Items.Cast<VideoGeneratorCandidate>().Single(c => c.Id == "minimax-h3");
                picker.UpdateLayout(); popupHeight = picker.ActualHeight; SaveImage(picker.Content as FrameworkElement, language + "-picker");
                Find<Button>(picker, "VideoPrep.ModelPicker.Apply").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));
            choose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.IsLessThan(500, popupHeight);
            Assert.IsTrue(VideoGeneratorPlan.CanResume(control.Session.Project));
            Assert.AreEqual("minimax-h3", VideoGeneratorPlan.Selected(control.Session.Project)!.Settings.ModelId);
            Assert.AreEqual("finish", control.Session.Project.CurrentStep);
            control.UpdateLayout(); SaveImage(control, language + "-finish");
            Assert.IsFalse(ScenarioNavigationTests.LogicalDescendants(control).OfType<TextBox>().Any(t => AutomationProperties.GetAutomationId(t).StartsWith("VideoPrep.Generator.", StringComparison.Ordinal)));
            VideoGeneratorSettings? route = null; control.GeneratorRequested += value => route = value;
            Assert.IsTrue(control.Next()); Assert.AreEqual("minimax-h3", route!.ModelId);
            Assert.IsFalse(ScenarioNavigationTests.LogicalDescendants(control).OfType<TextBlock>().Any(t => t.Text.StartsWith("VideoPrep.", StringComparison.Ordinal)));
            using var preview = new PreviewLifetime(new VideoBoardPreviewWindow(owner, k => k, control.Session.Project));
            preview.Window.Show(); preview.Window.Seek(4.25); Assert.AreEqual(4.25, preview.Window.Position);
            preview.Window.Play(); await Task.Delay(90); preview.Window.Pause(); var position = preview.Window.Position; preview.Window.Pause(); Assert.AreEqual(position, preview.Window.Position);
        }
        finally { owner.Close(); }
    });
    private sealed class PreviewLifetime(VideoBoardPreviewWindow window) : IDisposable
    { public VideoBoardPreviewWindow Window { get; } = window; public void Dispose() => Window.Close(); }
    private static void SaveImage(FrameworkElement? view, string name)
    {
        var folder = Environment.GetEnvironmentVariable("LOPATA_FINISH_POPUP_IMAGES"); if (folder is null || view is null) return;
        view.UpdateLayout(); var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth), (int)Math.Ceiling(view.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view); var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        System.IO.Directory.CreateDirectory(folder); using var output = System.IO.File.Create(System.IO.Path.Combine(folder, name + ".png")); encoder.Save(output);
    }
    [TestMethod, DataRow("ltx-2.5"), DataRow("wan-vace-1.3b")]
    public Task RetiredGeneratorPageReturnsToFinishWithoutReplacingHistoryAndRequiresANewChoice(string modelId) => OnUi(async () =>
    {
        using var f = new VideoPreparationTests.TestFiles(); var p = VideoPreparationFinishTests.ReadyProject();
        p.CurrentStep = "generator"; VideoGeneratorPlan.SaveChoice(p, new(modelId, 1920, 1080, 30, 42, "saved notes"));
        VideoPreparationArchive.Save(p, f.File("legacy.lopataAIVid")); var s = new VideoPreparationSession(f.File("recovery")); s.Load(f.File("legacy.lopataAIVid"));
        var l = new LocalizationService(); l.Load("ru"); var control = new VideoPreparationControl(l.T, s); var owner = new Window { Content = control }; owner.Show();
        try
        {
            Assert.AreEqual("finish", s.Project.CurrentStep); var oldChoice = VideoGeneratorPlan.Selected(s.Project)!;
            var saved = oldChoice.Settings; var count = s.Project.Generator!.History.Count;
            Assert.IsFalse(VideoGeneratorPlan.CanResume(s.Project)); Assert.IsFalse(VideoGeneratorPlan.IsAvailable(saved.ModelId));
            void Decide(bool apply) => owner.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                var picker = owner.OwnedWindows.OfType<VideoGeneratorChoiceWindow>().Single();
                var list = Find<System.Windows.Controls.ListBox>(picker, "VideoPrep.ModelPicker.List");
                CollectionAssert.AreEqual(new[] { "minimax-h3", VideoH3LiteCatalog.ModelId }, list.Items.Cast<VideoGeneratorCandidate>().Select(c => c.Id).ToArray());
                Assert.AreEqual("minimax-h3", ((VideoGeneratorCandidate)list.SelectedItem).Id);
                Find<Button>(picker, "VideoPrep.ModelPicker." + (apply ? "Apply" : "Cancel")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));
            Decide(false);
            Find<Button>(control, "VideoPrep.FinishChooseModel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.AreEqual(saved, VideoGeneratorPlan.Selected(s.Project)!.Settings); Assert.AreEqual(count, s.Project.Generator.History.Count);
            Decide(false);
            Assert.IsFalse(control.Next()); Assert.AreEqual("finish", s.Project.CurrentStep);
            Assert.AreEqual(oldChoice.Id, s.Project.Generator.SelectedId); Assert.IsFalse(VideoGeneratorPlan.CanResume(s.Project));
            VideoGeneratorSettings? route = null; control.GeneratorRequested += value => route = value;
            Decide(true); Assert.IsTrue(control.Next()); Assert.AreEqual("minimax-h3", route!.ModelId);
            Assert.AreEqual(count + 1, s.Project.Generator.History.Count);
            Assert.AreEqual(saved, s.Project.Generator.History.Single(c => c.Id == oldChoice.Id).Settings);
            Assert.IsTrue(VideoGeneratorPlan.CanResume(s.Project));
            await Task.CompletedTask;
        }
        finally { owner.Close(); }
    });
    [TestMethod, DataRow(false), DataRow(true)]
    public Task ReopenedPreparationCanMoveBothWaysWithoutRewritingUnchangedMedia(bool installed) => OnUi(async () =>
    {
        using var f = new VideoPreparationTests.TestFiles(); var control = Control(f);
        var path = Environment.GetEnvironmentVariable("LOPATA_REOPEN_PROJECT");
        if (installed && string.IsNullOrEmpty(path)) Assert.Inconclusive("An explicit test copy is required.");
        if (!installed)
        {
            var candidate = VideoPreparationFinishTests.ReadyProject(); candidate.CurrentStep = "package";
            VideoGeneratorPlan.SaveChoice(candidate, new("minimax-h3", 1280, 720, 24, -1, "saved settings"));
            candidate.Generator!.FinalReached = true; candidate.Generator.PreparationStep = "package";
            VideoWorldTests.AddMetadataOnlyRevision(candidate);
            path = f.File("reopen.lopataAIVid"); VideoPreparationArchive.Save(candidate, path);
        }
        var owner = new Window { Content = control, Width = 1220, Height = 880 }; owner.Show();
        try
        {
            _ = owner.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                var dialog = owner.OwnedWindows.OfType<VideoPreparationDecisionWindow>().Single();
                Find<Button>(dialog, "VideoPrep.Decision.preparation").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));
            Assert.IsTrue(await control.OpenPathWhenReadyAsync(path!));
            var session = control.Session; var p = session.Project;
            Assert.IsTrue(VideoPreparationReadiness.Ready(p), string.Join(",", VideoPreparationReadiness.Check(p).Where(i => i.Blocking).Select(i => i.Key)));
            var english = p.World!.Working.EnglishText; var translation = p.World.Working.TranslationRevisionId;
            Assert.IsTrue(control.Save());
            var archiveWrite = System.IO.File.GetLastWriteTimeUtc(path!); var modified = p.ModifiedUtc;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            using (var locked = new System.IO.FileStream(path!, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.None))
            using (var recovery = new System.IO.FileStream(session.RecoveryPath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.None))
            {
                while (p.CurrentStep != "form") Assert.IsTrue(control.BackStepIfNeeded());
                while (p.CurrentStep != "finish") Assert.IsTrue(await control.NextAsync(), "Could not leave " + p.CurrentStep);
                Assert.IsFalse(session.IsDirty); Assert.AreEqual(modified, p.ModifiedUtc);
                Assert.AreEqual(english, p.World.Working.EnglishText); Assert.AreEqual(translation, p.World.Working.TranslationRevisionId);
            }
            watch.Stop(); Assert.AreEqual(archiveWrite, System.IO.File.GetLastWriteTimeUtc(path!));
            if (Environment.GetEnvironmentVariable("LOPATA_REOPEN_EVIDENCE") is { Length: > 0 } evidence)
            {
                System.IO.Directory.CreateDirectory(evidence);
                System.IO.File.WriteAllText(System.IO.Path.Combine(evidence, $"navigation-{installed}.json"),
                    System.Text.Json.JsonSerializer.Serialize(new { installed, seconds = watch.Elapsed.TotalSeconds, state = p.CurrentStep,
                        images = p.References?.Images.Count, attachments = p.Attachments.Count, ready = VideoPreparationReadiness.Ready(p), unchangedArchive = true }));
            }
        }
        finally { owner.Close(); }
    });
    [TestMethod]
    public Task OpenRouteGatesResumeAndCancelRetainsExistingSession() => OnUi(async () =>
    {
        using var f = new VideoPreparationTests.TestFiles(); var control = Control(f); var original = control.Session.Project;
        var candidate = VideoPreparationFinishTests.ReadyProject(); candidate.CurrentStep = "finish"; candidate.Generator = new() { FinalReached = true };
        var owner = new Window { Content = control }; owner.Show();
        try
        {
            bool disabled = false;
            void Decide(string choice) => owner.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                var d = owner.OwnedWindows.OfType<VideoPreparationDecisionWindow>().Single(); disabled = !Find<Button>(d, "VideoPrep.Decision.generator").IsEnabled;
                Find<Button>(d, "VideoPrep.Decision." + choice).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));
            Decide("preparation"); Assert.AreEqual("storyboard", control.AskOpenRoute(candidate)); Assert.IsTrue(disabled);
            VideoGeneratorPlan.SaveChoice(candidate, new("minimax-h3", 1280, 720, 24, -1, ""));
            Decide("generator"); Assert.AreEqual("generator", control.AskOpenRoute(candidate)); Assert.IsFalse(disabled);
            var path = f.File("final.lopataAIVid"); VideoPreparationArchive.Save(candidate, path);
            Decide("cancel"); Assert.IsFalse(await control.OpenPathWhenReadyAsync(path)); Assert.AreSame(original, control.Session.Project);
            Decide("preparation"); Assert.IsTrue(await control.OpenPathWhenReadyAsync(path)); Assert.AreEqual(candidate.Id, control.Session.Project.Id);
            Assert.AreEqual("storyboard", control.Session.Project.CurrentStep);
            VideoGeneratorSettings? route = null; control.GeneratorRequested += value => route = value;
            Decide("generator"); Assert.IsTrue(await control.OpenPathWhenReadyAsync(path));
            Assert.AreEqual("minimax-h3", route!.ModelId); Assert.AreEqual("finish", control.Session.Project.CurrentStep);
        }
        finally { owner.Close(); }
    });
}
