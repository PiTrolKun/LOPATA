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
    [TestMethod, DataRow("ru", true), DataRow("en", false)]
    public Task FinishUsesHorizontalActionsAndPopupThenRoutesToSelectedModel(string language, bool dark) => OnUi(async () =>
    {
        using var f = new VideoPreparationTests.TestFiles(); var control = Control(f, language);
        control.Resources["TextPrimaryBrush"] = new SolidColorBrush(dark ? Colors.White : Colors.Black);
        var owner = new Window { Content = control, Width = 1220, Height = 880 }; owner.Show();
        owner.Resources["TextPrimaryBrush"] = new SolidColorBrush(dark ? Colors.White : Colors.Black);
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
                var list = Find<System.Windows.Controls.ListBox>(picker, "VideoPrep.ModelPicker.List"); list.SelectedIndex = 1;
                picker.UpdateLayout(); popupHeight = picker.ActualHeight; SaveImage(picker.Content as FrameworkElement, language + "-picker");
                Find<Button>(picker, "VideoPrep.ModelPicker.Apply").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));
            choose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.IsLessThan(350, popupHeight);
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
    [TestMethod]
    public Task LegacyGeneratorPageReturnsToFinishAndCancelKeepsSavedSettings() => OnUi(async () =>
    {
        using var f = new VideoPreparationTests.TestFiles(); var p = VideoPreparationFinishTests.ReadyProject();
        p.CurrentStep = "generator"; VideoGeneratorPlan.SaveChoice(p, new("ltx-2.5", 1920, 1080, 30, 42, "saved notes"));
        VideoPreparationArchive.Save(p, f.File("legacy.lopataAIVid")); var s = new VideoPreparationSession(f.File("recovery")); s.Load(f.File("legacy.lopataAIVid"));
        var l = new LocalizationService(); l.Load("ru"); var control = new VideoPreparationControl(l.T, s); var owner = new Window { Content = control }; owner.Show();
        try
        {
            Assert.AreEqual("finish", s.Project.CurrentStep); var saved = VideoGeneratorPlan.Selected(s.Project)!.Settings; var count = s.Project.Generator!.History.Count;
            _ = owner.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                var picker = owner.OwnedWindows.OfType<VideoGeneratorChoiceWindow>().Single();
                Find<System.Windows.Controls.ListBox>(picker, "VideoPrep.ModelPicker.List").SelectedIndex = 1;
                Find<Button>(picker, "VideoPrep.ModelPicker.Cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));
            Find<Button>(control, "VideoPrep.FinishChooseModel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.AreEqual(saved, VideoGeneratorPlan.Selected(s.Project)!.Settings); Assert.AreEqual(count, s.Project.Generator.History.Count);
            Assert.IsFalse(control.Next()); Assert.AreEqual("finish", s.Project.CurrentStep);
            await Task.CompletedTask;
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
            VideoGeneratorPlan.SaveChoice(candidate, new("ltx-2.5", 1280, 720, 24, -1, ""));
            Decide("generator"); Assert.AreEqual("generator", control.AskOpenRoute(candidate)); Assert.IsFalse(disabled);
            var path = f.File("final.lopataAIVid"); VideoPreparationArchive.Save(candidate, path);
            Decide("cancel"); Assert.IsFalse(await control.OpenPathWhenReadyAsync(path)); Assert.AreSame(original, control.Session.Project);
            Decide("preparation"); Assert.IsTrue(await control.OpenPathWhenReadyAsync(path)); Assert.AreEqual(candidate.Id, control.Session.Project.Id);
            Assert.AreEqual("storyboard", control.Session.Project.CurrentStep);
            VideoGeneratorSettings? route = null; control.GeneratorRequested += value => route = value;
            Decide("generator"); Assert.IsTrue(await control.OpenPathWhenReadyAsync(path));
            Assert.AreEqual("ltx-2.5", route!.ModelId); Assert.AreEqual("finish", control.Session.Project.CurrentStep);
        }
        finally { owner.Close(); }
    });
}
