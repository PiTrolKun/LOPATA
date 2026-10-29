using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Services;
using AIHub.Services.VideoPreparation;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using ComboBox = System.Windows.Controls.ComboBox;
using ListBox = System.Windows.Controls.ListBox;
using TextBox = System.Windows.Controls.TextBox;
using MenuItem = System.Windows.Controls.MenuItem;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoH3WorkspaceUiTests
{
    private static readonly Lazy<Dispatcher> Ui = new(() =>
    {
        var ready = new TaskCompletionSource<Dispatcher>(); var thread = new Thread(() =>
        { var d = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(d)); ready.SetResult(d); Dispatcher.Run(); }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return ready.Task.GetAwaiter().GetResult();
    });
    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject => ScenarioNavigationTests.LogicalDescendants(root)
        .OfType<T>().Single(x => AutomationProperties.GetAutomationId(x) == "VideoH3.Workspace." + id);
    private static void Click(DependencyObject root, string id) => Find<Button>(root, id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    [TestMethod, DataRow("ru", true, 1500d, 900d), DataRow("en", false, 880d, 660d)]
    public Task CompactPresetsResultOnlyViewAndActionsWorkInBothThemes(string language, bool dark, double width, double height) => Ui.Value.InvokeAsync(async () =>
    {
        using var f = new VideoPreparationTests.TestFiles(); var l = new LocalizationService(); l.Load(language);
        var p = VideoPreparationFinishTests.ReadyProject(); var preparation = VideoShots.Snapshot(p.Shots!.Scenes);
        var png = f.File("reference.png");
        var bitmap = new System.Windows.Media.Imaging.WriteableBitmap(240, 160, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new byte[240 * 160 * 4]; for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 110; pixels[i + 1] = 90; pixels[i + 2] = 40; pixels[i + 3] = 255; }
        bitmap.WritePixels(new Int32Rect(0, 0, 240, 160), pixels, 240 * 4, 0);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var output = File.Create(png)) encoder.Save(output);
        var imported = await VideoReferenceFiles.ImportAsync(png, f.File("refs"));
        p.References!.Images.Add(imported.Image); p.Attachments.Add(new(imported.Path, imported.Image.AttachmentRole)); p.Storyboard!.Frames[0].ImageId = imported.Image.Id;
        p.Generator ??= new(); p.Generator.H3 = new();
        p.Generator.H3.Settings.Steps = 27; p.Generator.H3.Settings.Width = 1024;
        p.Generator.H3.SelectedShotId = p.Shots.Scenes[0].Shots[0].Id;
        p.Generator.H3.Plans[p.Generator.H3.SelectedShotId] = new() { Prompt = "Previously saved plan prompt", Seconds = 7.25 };
        var plans = JsonSerializer.Serialize(p.Generator.H3.Plans);
        var board = JsonSerializer.Serialize(p.Storyboard);
        var changed = 0; var save = 0; var back = 0; var home = 0; var export = 0; var model = 0;
        var page = new VideoH3WorkspaceControl(l.T, p, () => changed++);
        page.SaveRequested += () => { VideoPreparationArchive.Save(p, f.File("saved.lopataAIVid")); save++; };
        page.BackRequested += () => back++; page.HomeRequested += () => home++; page.ExportRequested += () => export++; page.ChangeModelRequested += () => model++;
        var owner = new Window { Content = page, Width = width, Height = height };
        owner.Resources["TextPrimaryBrush"] = dark ? Brushes.White : Brushes.Black;
        owner.Resources["TextSecondaryBrush"] = dark ? Brushes.LightGray : Brushes.DimGray;
        owner.Resources["WindowBackgroundBrush"] = dark ? new SolidColorBrush(Color.FromRgb(16, 24, 39)) : Brushes.White;
        owner.Resources["PanelBrush"] = dark ? new SolidColorBrush(Color.FromRgb(24, 33, 49)) : Brushes.WhiteSmoke;
        owner.Resources["SecondaryButtonBackgroundBrush"] = owner.Resources["PanelBrush"];
        owner.Resources["LineBrush"] = Brushes.Gray; owner.Resources["AccentBrush"] = Brushes.RoyalBlue;
        owner.Resources["UiBodyFontSize"] = 14d; owner.Resources["UiSectionFontSize"] = 18d;
        owner.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/VideoPreparationResources.xaml", UriKind.Relative) });
        try
        {
            owner.Show(); owner.UpdateLayout();
            Assert.AreEqual(0, Find<ListBox>(page, "Timeline").Items.Count);
            Assert.IsFalse(Find<Button>(page, "PlayPreview").IsEnabled);
            Assert.IsFalse(ScenarioNavigationTests.LogicalDescendants(page).OfType<System.Windows.Controls.Image>().Any(x => x.Source is not null));
            Assert.IsFalse(Find<Button>(page, "PreviousResult").IsEnabled); Assert.IsFalse(Find<Button>(page, "NextResult").IsEnabled);
            Assert.AreEqual(l.T("VideoPrep.H3EmptyWholeVideo"), Find<TextBlock>(page, "ResultMode").Text);
            Assert.IsFalse(ScenarioNavigationTests.LogicalDescendants(page).Any(x => AutomationProperties.GetAutomationId(x) == "VideoH3.Workspace.H3Prompt"));
            Assert.AreEqual(owner.Resources["PanelBrush"], Find<Button>(page, "Save").Background);
            Assert.IsFalse(Find<Button>(page, "Generate").IsEnabled); Assert.IsFalse(Find<Button>(page, "H3SendEditor").IsEnabled);
            var steps = Find<ComboBox>(page, "H3Steps"); Assert.AreEqual("27", steps.SelectedItem!.ToString());
            steps.SelectedIndex = steps.Items.Cast<object>().ToList().FindIndex(x => x.ToString() == "32");
            Assert.IsFalse(ScenarioNavigationTests.LogicalDescendants(page).Any(x => AutomationProperties.GetAutomationId(x) == "VideoH3.Workspace.H3RunMode"));
            Assert.AreEqual(l.T("VideoPrep.H3EmptyWholeVideo"), Find<TextBlock>(page, "ResultMode").Text);
            Assert.AreEqual(0, Find<ListBox>(page, "Timeline").Items.Count);
            Assert.AreEqual(32, p.Generator.H3.Settings.Steps); Assert.AreEqual("automatic", p.Generator.H3.Settings.RunMode);
            Assert.AreEqual(preparation, VideoShots.Snapshot(p.Shots.Scenes));
            Assert.AreEqual(board, JsonSerializer.Serialize(p.Storyboard)); Assert.AreEqual(plans, JsonSerializer.Serialize(p.Generator.H3.Plans));
            Find<TextBox>(page, "H3Seed").Text = "not a number";
            Click(page, "Save"); Click(page, "Back"); Click(page, "Home"); Assert.AreEqual(0, save + back + home);
            Assert.AreEqual("not a number", Find<TextBox>(page, "H3Seed").Text); Assert.AreEqual(32, p.Generator.H3.Settings.Steps);
            Find<TextBox>(page, "H3Seed").Text = "123";
            steps.SelectedIndex = steps.Items.Cast<object>().ToList().FindIndex(x => x.ToString() == "24");
            // Legacy custom dimensions survive removal of the custom-size controls.
            Assert.IsFalse(ScenarioNavigationTests.LogicalDescendants(page).Any(x => AutomationProperties.GetAutomationId(x) == "VideoH3.Workspace.H3Width"));
            Assert.IsTrue(Find<ComboBox>(page, "H3Resolution").SelectedItem!.ToString()!.StartsWith("1024 × ", StringComparison.Ordinal));
            Assert.IsFalse(ScenarioNavigationTests.LogicalDescendants(page).Any(x => AutomationProperties.GetAutomationId(x) == "VideoH3.Workspace.H3LoraFile"));
            Assert.IsTrue(Find<ComboBox>(page, "H3Quality").SelectedItem!.ToString()!.EndsWith('%'));
            Assert.AreEqual(new Thickness(0), Find<Button>(page, "ChangeModel").BorderThickness);
            Click(page, "Save"); Click(page, "ExportPreparation"); Click(page, "ChangeModel");
            Assert.AreEqual(1, save); Assert.AreEqual(1, export); Assert.AreEqual(1, model); Assert.IsGreaterThan(0, changed);
            var cancelled = 0; page.CancelExportRequested += () => cancelled++;
            page.SetBusy(true); Assert.IsTrue(Find<Button>(page, "CancelExport").IsEnabled); Assert.IsFalse(page.TryCommit());
            Click(page, "CancelExport"); Assert.AreEqual(1, cancelled); page.SetBusy(false);
            page.Notify(l.T("VideoPrep.H3SettingsSaved"));
            var loaded = VideoPreparationArchive.Load(f.File("saved.lopataAIVid"), f.File("readback"));
            Assert.AreEqual(24, loaded.Generator!.H3!.Settings.Steps); Assert.AreEqual(plans, JsonSerializer.Serialize(loaded.Generator.H3.Plans));
            Assert.AreEqual(123L, loaded.Generator.H3.Settings.Seed); Assert.AreEqual(1024, loaded.Generator.H3.Settings.Width);
            Assert.AreEqual(0, Find<ListBox>(page, "Timeline").Items.Count);
            SaveImage(page, language + "-workspace");
            Click(page, "ExpandPreview"); owner.UpdateLayout(); Click(page, "ExpandPreview");
            foreach (var expander in ScenarioNavigationTests.LogicalDescendants(page).OfType<Expander>()) expander.IsExpanded = true;
            owner.UpdateLayout(); var scroll = Find<ScrollViewer>(page, "SettingsScroll"); scroll.ScrollToEnd(); owner.UpdateLayout();
            Assert.IsGreaterThan(0d, scroll.VerticalOffset);
            Assert.IsTrue(Find<Button>(page, "Back").TransformToAncestor(page).Transform(new Point()).Y < page.ActualHeight);
            SaveImage(page, language + "-settings"); scroll.ScrollToTop(); owner.UpdateLayout();
            Click(page, "Back"); Click(page, "Home"); Assert.AreEqual(1, back); Assert.AreEqual(1, home);
            page.GenerationAvailable = true; page.SetGenerating(true);
            page.GenerationProgress("[H3 event] {\"kind\":\"pass\",\"index\":3,\"total\":8,\"item_index\":3,\"item_total\":8}");
            page.GenerationProgress("[H3 event] {\"kind\":\"stage\",\"text\":\"sampling\"}");
            page.GenerationProgress("[H3 event] {\"kind\":\"progress\",\"value\":10,\"total\":20}");
            scroll.ScrollToTop(); owner.UpdateLayout(); SaveImage(page, language + "-active"); page.SetGenerating(false);
        }
        finally { owner.Close(); }
    }).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(45));
    [TestMethod]
    public Task GenerationActionsAndActualResultKindsRemainSeparate() => Ui.Value.InvokeAsync(() =>
    {
        using var f = new VideoPreparationTests.TestFiles(); var l = new LocalizationService(); l.Load("ru");
        var p = VideoPreparationFinishTests.ReadyProject(); var h = VideoH3WorkspaceState.Ensure(p);
        var run = new VideoH3Run { InputHash = new string('a', 64) }; h.Runs.Add(run);
        var source = f.File("synthetic.mp4"); File.WriteAllText(source, "Synthetic UI fixture, not an inference result");
        var role = "h3-run/" + run.Id + "/fixture"; p.Attachments.Add(new(source, role));
        foreach (var kind in new[] { "fragment", "fragment", "whole" })
            run.Results.Add(new(Guid.NewGuid().ToString("N"), kind, role, 5, DateTimeOffset.UtcNow, [new("fixture", "fixture", "fixture", "test")]));
        var page = new VideoH3WorkspaceControl(l.T, p) { GenerationAvailable = true };
        var generated = 0; var cancelled = 0; var paused = 0; var sent = 0;
        page.GenerateRequested += () => generated++; page.CancelGenerationRequested += () => cancelled++;
        page.PauseGenerationRequested += () => paused++; page.SendEditorRequested += _ => sent++; page.ReloadResults();
        var owner = new Window { Content = page, Width = 1200, Height = 800 };
        try
        {
            owner.Show(); owner.UpdateLayout(); Assert.AreEqual(1, Find<ListBox>(page, "Timeline").Items.Count);
            Assert.IsTrue(Find<Button>(page, "Generate").IsEnabled); Click(page, "Generate"); Assert.AreEqual(1, generated);
            Assert.IsTrue(Find<Button>(page, "Generate").TransformToAncestor(page).Transform(new Point()).Y < page.ActualHeight);
            Assert.AreSame(VisualTreeHelper.GetParent(Find<Button>(page, "Generate")), VisualTreeHelper.GetParent(Find<Button>(page, "PauseGeneration")));
            Assert.AreSame(VisualTreeHelper.GetParent(Find<Button>(page, "Generate")), VisualTreeHelper.GetParent(Find<Button>(page, "CancelExport")));
            Assert.IsTrue(Find<Button>(page, "H3SendEditor").IsEnabled); Click(page, "H3SendEditor"); Assert.AreEqual(1, sent);
            Assert.AreEqual(1, Find<ListBox>(page, "Timeline").Items.Count);
            page.SetGenerating(true); Assert.IsFalse(Find<Button>(page, "Generate").IsEnabled);
            Assert.IsTrue(Find<Button>(page, "CancelExport").IsEnabled); Click(page, "CancelExport"); Click(page, "PauseGeneration");
            Assert.AreEqual(1, cancelled); Assert.AreEqual(1, paused);
            page.SetGenerating(false, pending: true); Assert.AreEqual("▶", Find<Button>(page, "PauseGeneration").Content);
            page.SetGenerating(false); Assert.IsTrue(Find<Button>(page, "Generate").IsEnabled);
            Assert.IsTrue(page.TryCommit()); Assert.AreEqual(3, p.Generator!.H3!.Runs.Single().Results.Count);
        }
        finally { owner.Close(); }
    }).Task.WaitAsync(TimeSpan.FromSeconds(30));

    [TestMethod]
    public Task ScheduledStartDialogAndBusyStatePreserveTheChosenTime() => Ui.Value.InvokeAsync(() =>
    {
        var l = new LocalizationService(); l.Load("ru");
        var p = VideoPreparationFinishTests.ReadyProject(); var page = new VideoH3WorkspaceControl(l.T, p) { GenerationAvailable = true };
        var owner = new Window { Content = page, Width = 1100, Height = 750 };
        owner.Resources["WindowBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(16, 24, 39));
        owner.Resources["PanelBrush"] = new SolidColorBrush(Color.FromRgb(24, 33, 49));
        owner.Resources["TextPrimaryBrush"] = Brushes.White;
        owner.Resources["TextSecondaryBrush"] = Brushes.LightGray;
        owner.Resources["LineBrush"] = Brushes.Gray;
        owner.Resources["AccentBrush"] = Brushes.RoyalBlue;
        owner.Resources["SecondaryButtonBackgroundBrush"] = owner.Resources["PanelBrush"];
        owner.Resources["UiBodyFontSize"] = 14d; owner.Resources["UiSectionFontSize"] = 18d;
        DateTimeOffset? scheduled = null; page.ScheduleGenerationRequested += due => scheduled = due;
        try
        {
            owner.Show(); owner.UpdateLayout();
            var start = Find<Button>(page, "Generate"); Assert.AreEqual(2, start.ContextMenu!.Items.Count);
            var before = DateTimeOffset.Now;
            Ui.Value.BeginInvoke(() =>
            {
                var dialog = owner.OwnedWindows.Cast<Window>().Single(w => AutomationProperties.GetAutomationId(w) == "VideoH3.Schedule.Window");
                var delay = ScenarioNavigationTests.LogicalDescendants(dialog).OfType<TextBox>().Single(x => AutomationProperties.GetAutomationId(x) == "VideoH3.Schedule.Delay");
                delay.Text = "30"; dialog.UpdateLayout(); SaveImage(dialog, "schedule-delay");
                ScenarioNavigationTests.LogicalDescendants(dialog).OfType<Button>().Single(x => AutomationProperties.GetAutomationId(x) == "VideoH3.Schedule.Apply").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            ((MenuItem)start.ContextMenu.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.IsNotNull(scheduled); Assert.IsTrue(scheduled > before.AddMinutes(29) && scheduled < DateTimeOffset.Now.AddMinutes(31));
            var cancelled = 0; page.CancelGenerationRequested += () => cancelled++;
            page.SetScheduled(scheduled); Assert.IsFalse(start.IsEnabled); Assert.IsFalse(page.TryCommit());
            Assert.AreEqual(Visibility.Collapsed, Find<Button>(page, "PauseGeneration").Visibility);
            Click(page, "CancelExport"); Assert.AreEqual(1, cancelled);
            page.SetScheduled(null); page.SetGenerating(false); Assert.IsTrue(start.IsEnabled);
            scheduled = null;
            Ui.Value.BeginInvoke(() =>
            {
                var dialog = owner.OwnedWindows.Cast<Window>().Single(w => AutomationProperties.GetAutomationId(w) == "VideoH3.Schedule.Window");
                ScenarioNavigationTests.LogicalDescendants(dialog).OfType<DatePicker>().Single().SelectedDate = DateTime.Today.AddDays(1);
                ScenarioNavigationTests.LogicalDescendants(dialog).OfType<TextBox>().Single(x => AutomationProperties.GetAutomationId(x) == "VideoH3.Schedule.Time").Text = "12:34";
                dialog.UpdateLayout(); SaveImage(dialog, "schedule-time");
                ScenarioNavigationTests.LogicalDescendants(dialog).OfType<Button>().Single(x => AutomationProperties.GetAutomationId(x) == "VideoH3.Schedule.Apply").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            ((MenuItem)start.ContextMenu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.IsNotNull(scheduled); Assert.AreEqual(DateTime.Today.AddDays(1).AddHours(12).AddMinutes(34), scheduled.Value.LocalDateTime);
        }
        finally { owner.Close(); }
    }).Task.WaitAsync(TimeSpan.FromSeconds(30));

    [TestMethod]
    public Task ProgressShowsTheOverallPlanAndIgnoresDecoderStepsForSamplingForecast() => Ui.Value.InvokeAsync(() =>
    {
        var l = new LocalizationService(); l.Load("en"); var page = new VideoH3WorkspaceControl(l.T, VideoPreparationFinishTests.ReadyProject());
        page.SetGenerating(true);
        page.GenerationProgress("[H3 event] {\"kind\":\"pass\",\"index\":2,\"total\":8,\"item_index\":2,\"item_total\":8}");
        page.GenerationProgress("[H3 event] {\"kind\":\"stage\",\"text\":\"sampling\"}");
        page.GenerationProgress("[H3 event] {\"kind\":\"progress\",\"value\":1,\"total\":20,\"elapsed_seconds\":30}");
        page.GenerationProgress("[H3 event] {\"kind\":\"progress\",\"value\":2,\"total\":20,\"elapsed_seconds\":50}");
        var summary = Find<TextBlock>(page, "ProgressSummary").Text;
        StringAssert.Contains(summary, "2/8"); StringAssert.Contains(summary, "2/20");
        StringAssert.Contains(summary, l.T("VideoPrep.H3ProgressRemainingSampling"));
        page.GenerationProgress("[H3 event] {\"kind\":\"stage\",\"text\":\"decoding\"}");
        page.GenerationProgress("[H3 event] {\"kind\":\"progress\",\"value\":9,\"total\":9}");
        Assert.IsFalse(Find<TextBlock>(page, "ProgressSummary").Text.Contains("9/9", StringComparison.Ordinal));
        page.GenerationProgress("[H3 event] {\"kind\":\"done\",\"complete\":true}");
        Assert.AreEqual(100d, Find<H3FluidProgress>(page, "Progress").Value);
        page.SetGenerating(false);
    }).Task.WaitAsync(TimeSpan.FromSeconds(20));

    [TestMethod]
    public Task ElapsedSecondsAdvanceOnlyOnClockTicksAndRemainFrozenOnPause() => Ui.Value.InvokeAsync(() =>
    {
        var l = new LocalizationService(); l.Load("en");
        var page = new VideoH3WorkspaceControl(l.T, VideoPreparationFinishTests.ReadyProject());
        var summary = Find<TextBlock>(page, "ProgressSummary");
        var tick = typeof(VideoH3WorkspaceControl).GetMethod("ProgressTick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var clock = (System.Diagnostics.Stopwatch)typeof(VideoH3WorkspaceControl).GetField("_activeClock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(page)!;
        void Observe(double seconds) => page.GenerationProgress("[H3 event] " + JsonSerializer.Serialize(new { kind = "elapsed", elapsed_seconds = seconds }));
        void Tick() => tick.Invoke(page, new object?[] { null, EventArgs.Empty });
        string Shown() => summary.Text.Split(l.T("VideoPrep.H3ProgressElapsed") + " ", StringSplitOptions.None)[1].Split(" · ", StringSplitOptions.None)[0];
        try
        {
            page.SetGenerating(true); Observe(12.1); Tick(); Assert.AreEqual("0:00:12", Shown());
            Observe(12.9); page.GenerationProgress("[H3 event] {\"kind\":\"gpu\",\"utilization\":77}");
            Assert.AreEqual("0:00:12", Shown());
            Observe(13.1); Assert.AreEqual("0:00:12", Shown()); Tick(); Assert.AreEqual("0:00:13", Shown());
            // A delayed observation must not move the visible counter backwards.
            Observe(12.5); Tick(); Assert.AreEqual("0:00:13", Shown());
            page.SetGenerating(false, pending: true); Assert.IsFalse(clock.IsRunning);
            Observe(13.5); Assert.IsFalse(clock.IsRunning);
            page.SetGenerating(true); Observe(75.1); Tick(); Assert.AreEqual("0:01:15", Shown());
            page.GenerationProgress("[H3 event] {\"kind\":\"done\",\"complete\":true}");
            page.SetGenerating(false); page.SetGenerating(true); Assert.AreEqual("0:00:00", Shown());
        }
        finally { page.SetGenerating(false); }
    }).Task.WaitAsync(TimeSpan.FromSeconds(20));

    [TestMethod]
    public Task FinalRefinementShowsItsOwnSegmentsAndOnlySamplingAdvancesSteps() => Ui.Value.InvokeAsync(() =>
    {
        var l = new LocalizationService(); l.Load("en");
        string Localize(string key) => key == "VideoPrep.H3ProgressRefinement" ? "Refinement {0}/{1}" : l.T(key);
        var page = new VideoH3WorkspaceControl(Localize, VideoPreparationFinishTests.ReadyProject());
        var summary = Find<TextBlock>(page, "ProgressSummary"); var fluid = Find<H3FluidProgress>(page, "Progress");
        void Message(string json) => page.GenerationProgress("[H3 event] " + json);
        try
        {
            page.SetGenerating(true);
            Message("{\"kind\":\"pass\",\"index\":8,\"total\":8,\"item_index\":8,\"item_total\":8}");
            Message("{\"kind\":\"stage\",\"text\":\"sampling\"}");
            Message("{\"kind\":\"progress\",\"value\":20,\"total\":20}");
            StringAssert.Contains(summary.Text, "8/8"); StringAssert.Contains(summary.Text, "20/20");
            Message("{\"kind\":\"stage\",\"text\":\"assembling\"}");
            Assert.IsFalse(summary.Text.Contains("8/8", StringComparison.Ordinal)); Assert.IsFalse(summary.Text.Contains("20/20", StringComparison.Ordinal));
            Assert.AreEqual(93d, fluid.Value);
            Message("{\"kind\":\"refinement-segments\",\"total\":2}");
            Message("{\"kind\":\"refinement\",\"index\":1,\"total\":2}");
            StringAssert.Contains(summary.Text, "Refinement 1/2");
            Message("{\"kind\":\"progress\",\"value\":9,\"total\":9}");
            Assert.AreEqual(93d, fluid.Value); Assert.IsFalse(summary.Text.Contains("9/9", StringComparison.Ordinal));
            Message("{\"kind\":\"stage\",\"text\":\"refinement-sampling\"}");
            Message("{\"kind\":\"progress\",\"value\":5,\"total\":10}");
            StringAssert.Contains(summary.Text, "5/10"); Assert.AreEqual(94.5, fluid.Value, .001);
            Assert.IsFalse(summary.Text.Contains(l.T("VideoPrep.H3ProgressRemainingSampling"), StringComparison.Ordinal));
            Message("{\"kind\":\"progress\",\"value\":10,\"total\":10}");
            Message("{\"kind\":\"refinement\",\"index\":2,\"total\":2}");
            StringAssert.Contains(summary.Text, "Refinement 2/2"); Assert.AreEqual(96d, fluid.Value);
            Assert.IsFalse(summary.Text.Contains("10/10", StringComparison.Ordinal));
            Message("{\"kind\":\"stage\",\"text\":\"refinement-sampling\"}");
            Message("{\"kind\":\"progress\",\"value\":10,\"total\":10}");
            Assert.AreEqual(99d, fluid.Value);
            Message("{\"kind\":\"stage\",\"text\":\"refinement-rendering\"}");
            Message("{\"kind\":\"progress\",\"value\":1,\"total\":4}");
            Assert.AreEqual(99d, fluid.Value); Assert.IsFalse(summary.Text.Contains("1/4", StringComparison.Ordinal));
            Assert.IsFalse(summary.Text.Contains("10/10", StringComparison.Ordinal));
            Message("{\"kind\":\"done\",\"complete\":true}"); Assert.AreEqual(100d, fluid.Value);
        }
        finally { page.SetGenerating(false); }
    }).Task.WaitAsync(TimeSpan.FromSeconds(20));

    [TestMethod]
    public Task ExistingNativeMediaOpensInPlayerAndRetainsModelsWhenImportedByEditor() => Ui.Value.InvokeAsync(async () =>
    {
        var source = Environment.GetEnvironmentVariable("LOPATA_H3_PLAYBACK_FILE");
        if (source is null) { Assert.Inconclusive("Explicit independently generated H3 video required."); return; }
        using var f = new VideoPreparationTests.TestFiles(); using var audit = ComponentAudit.UseTestStore(f.File("audit"), enabled: false);
        await AIHub.Services.VideoEditor.VideoRuntime.PrepareAsync(default);
        var asset = await AIHub.Services.VideoEditor.VideoImport.ReadAsync(source, f.File("preview"), default);
        Assert.IsTrue(asset.Models.Any(m => m.Id.Contains("minimax_h3", StringComparison.Ordinal)));
        var p = VideoPreparationFinishTests.ReadyProject(); var h = VideoH3WorkspaceState.Ensure(p); h.Settings.RunMode = "automatic";
        var run = new VideoH3Run { InputHash = new string('a', 64) }; h.Runs.Add(run);
        var role = "h3-run/" + run.Id + "/media"; p.Attachments.Add(new(source, role));
        run.Results.Add(new(Guid.NewGuid().ToString("N"), "whole", role, asset.Duration, DateTimeOffset.UtcNow, asset.Models.ToArray()));
        var l = new LocalizationService(); l.Load("ru"); var page = new VideoH3WorkspaceControl(l.T, p);
        var player = ScenarioNavigationTests.LogicalDescendants(page).OfType<MediaElement>().Single();
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.MediaOpened += (_, _) => opened.TrySetResult(); player.MediaFailed += (_, e) => opened.TrySetException(e.ErrorException);
        var owner = new Window { Content = page, Width = 1100, Height = 750 };
        try
        {
            owner.Show(); await opened.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.IsTrue(Find<Button>(page, "PlayPreview").IsEnabled); Assert.IsGreaterThan(0, player.NaturalVideoWidth);
            Assert.IsTrue(Find<Button>(page, "H3ExportVideo").IsEnabled);
        }
        finally { owner.Close(); }
    }).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(40));

    private static void SaveImage(FrameworkElement page, string name)
    {
        var folder = Environment.GetEnvironmentVariable("LOPATA_H3_WORKSPACE_IMAGES"); if (folder is null) return;
        page.UpdateLayout(); var image = new System.Windows.Media.Imaging.RenderTargetBitmap((int)page.ActualWidth, (int)page.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(page); var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        Directory.CreateDirectory(folder); using var output = File.Create(Path.Combine(folder, name + ".png")); encoder.Save(output);
    }
}
