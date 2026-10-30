using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
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
using ComboBox = System.Windows.Controls.ComboBox;
using ListBox = System.Windows.Controls.ListBox;
using MenuItem = System.Windows.Controls.MenuItem;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoVaceWorkspaceUiTests
{
    private const string Prefix = "VideoVace.Workspace.";
    private const string Keys = "VideoPrep.VaceWorkspace.";
    private static readonly Lazy<Dispatcher> Ui = new(() =>
    {
        var ready = new TaskCompletionSource<Dispatcher>();
        var thread = new Thread(() => { var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher)); ready.SetResult(dispatcher); Dispatcher.Run(); }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return ready.Task.GetAwaiter().GetResult();
    });
    private static Task OnUi(Func<Task> action) => Ui.Value.InvokeAsync(action).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(45));
    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject => ScenarioNavigationTests.LogicalDescendants(root).OfType<T>()
        .Single(item => AutomationProperties.GetAutomationId(item) == Prefix + id);
    private static void Click(DependencyObject page, string id) => Find<Button>(page, id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static Window Owner(FrameworkElement page, bool dark, double width = 1400, double height = 850)
    {
        var window = new Window { Content = page, Width = width, Height = height, UseLayoutRounding = true,
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI") };
        window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/VideoPreparationResources.xaml", UriKind.Relative) });
        foreach (var (key, color) in new[] {
            ("TextPrimaryBrush", dark ? "#F8FAFC" : "#1F1F1F"), ("TextSecondaryBrush", dark ? "#AAB4C4" : "#5D6470"),
            ("WindowBackgroundBrush", dark ? "#111827" : "#F3F3F3"), ("PanelBrush", dark ? "#172033" : "#FFFFFF"),
            ("SecondaryButtonBackgroundBrush", dark ? "#111827" : "#F8F8F8"), ("LineBrush", dark ? "#2D374B" : "#DADDE3"), ("AccentBrush", "#2563EB") })
            window.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        window.Resources["UiBodyFontSize"] = 14d; window.Resources["UiSectionFontSize"] = 18d; return window;
    }
    private static VideoPreparationProject Project()
    {
        var project = VideoPreparationFinishTests.ReadyProject();
        VideoGeneratorPlan.SaveChoice(project, new("minimax-h3", 480, 864, 24, 42, "existing H3 history"));
        var h3 = VideoH3WorkspaceState.Ensure(project); h3.Settings.Steps = 32; h3.Settings.ExportName = "h3-kept";
        VideoGeneratorPlan.SaveChoice(project, new("wan-vace-1.3b", 832, 480, 16, -1, "VACE workspace")); return project;
    }
    [TestMethod, DataRow("ru", true, 1500d, 900d), DataRow("en", false, 900d, 650d)]
    public Task NativeCompactSettingsAndThemeDoNotExposeOtherGeneratorsOrPreparation(string language, bool dark, double width, double height) => OnUi(() =>
    {
        var localization = new LocalizationService(); localization.Load(language); var project = Project();
        var page = new VideoVaceWorkspaceControl(localization.T, project); var owner = Owner(page, dark, width, height);
        try
        {
            owner.Show(); owner.UpdateLayout();
            Assert.AreEqual(0, Find<ListBox>(page, "Timeline").Items.Count); Assert.IsFalse(Find<Button>(page, "PlayPreview").IsEnabled);
            Assert.IsFalse(Find<Button>(page, "Generate").IsEnabled); Assert.IsFalse(Find<Button>(page, "ExportVideo").IsEnabled);
            Assert.AreEqual(2, Find<ComboBox>(page, "Resolution").Items.Count);
            Assert.AreEqual(localization.T(Keys + "Landscape"), Find<ComboBox>(page, "Resolution").SelectedItem!.ToString());
            Assert.IsTrue(Find<ComboBox>(page, "Quality").SelectedItem!.ToString()!.EndsWith('%'));
            foreach (var forbidden in new[] { "Frames", "Fps", "Audio", "Lora", "RunMode", "CustomSize", "Width", "Height", "Upscale" })
                Assert.IsFalse(ScenarioNavigationTests.LogicalDescendants(page).Any(item => AutomationProperties.GetAutomationId(item) == Prefix + forbidden));
            Assert.IsFalse(ScenarioNavigationTests.LogicalDescendants(page).OfType<System.Windows.Controls.Image>().Any());
            Assert.IsFalse(ScenarioNavigationTests.LogicalDescendants(page).OfType<TextBlock>().Any(text => text.Text.StartsWith(Keys, StringComparison.Ordinal)), "All UI labels must be localized.");
            StringAssert.Contains(ScenarioNavigationTests.LogicalDescendants(page).OfType<TextBlock>().Single(text => text.Text == localization.T(Keys + "NewResultsHint")).Text,
                language == "ru" ? "новым результатам" : "new results");
            Assert.AreEqual(new Thickness(0), Find<Button>(page, "ChangeModel").BorderThickness);
            Assert.AreEqual(((SolidColorBrush)owner.Resources["SecondaryButtonBackgroundBrush"]).Color, ((SolidColorBrush)Find<Button>(page, "Save").Background).Color);
            foreach (var id in new[] { "Generate", "Back", "Home" })
            {
                var button = Find<Button>(page, id); var position = button.TransformToAncestor(page).Transform(new Point());
                Assert.IsTrue(position.X >= 0 && position.X + button.ActualWidth <= page.ActualWidth + 1, id + " overflows horizontally");
                Assert.IsTrue(position.Y >= 0 && position.Y + button.ActualHeight <= page.ActualHeight + 1, id + " is outside the page");
            }
            Save(page, language + "-workspace");
            foreach (var expander in ScenarioNavigationTests.LogicalDescendants(page).OfType<Expander>()) expander.IsExpanded = true;
            owner.UpdateLayout(); var settings = Find<ScrollViewer>(page, "SettingsScroll"); settings.ScrollToEnd(); owner.UpdateLayout();
            if (height <= 650) Assert.IsTrue(settings.VerticalOffset > 0, "The compact window must scroll through all expanded settings.");
            else Assert.IsTrue(settings.ScrollableHeight == 0 || settings.VerticalOffset > 0, "Overflowing settings must remain reachable; a large window may fit them all.");
            Save(page, language + "-settings-bottom");
            var files = Find<ScrollViewer>(page, "FilesScroll"); files.ScrollToEnd(); owner.UpdateLayout(); Save(page, language + "-files-bottom");
            Click(page, "ExpandPreview"); owner.UpdateLayout(); Click(page, "ExpandPreview");
        }
        finally { owner.Close(); }
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task SettingsAndManualValidationPreserveH3HistoryAndPreparation() => OnUi(() =>
    {
        var localization = new LocalizationService(); localization.Load("ru"); var project = Project();
        var h3 = JsonSerializer.Serialize(project.Generator!.H3); var history = JsonSerializer.Serialize(project.Generator.History);
        var preparation = JsonSerializer.Serialize(new { project.World, project.Shots, project.Storyboard });
        var changed = 0; var saved = 0; var back = 0; var home = 0; var generated = 0;
        var page = new VideoVaceWorkspaceControl(localization.T, project, () => changed++) { GenerationAvailable = true };
        page.SaveRequested += () => saved++; page.BackRequested += () => back++; page.HomeRequested += () => home++; page.GenerateRequested += () => generated++;
        var owner = Owner(page, true);
        try
        {
            owner.Show(); owner.UpdateLayout(); var resolution = Find<ComboBox>(page, "Resolution"); resolution.SelectedIndex = 1;
            Assert.AreEqual(480, project.Generator.Vace!.Settings.Width); Assert.AreEqual(832, project.Generator.Vace.Settings.Height);
            Find<TextBox>(page, "Seed").Text = "not a seed";
            Click(page, "Save"); Click(page, "Back"); Click(page, "Home"); Click(page, "Generate");
            Assert.AreEqual(0, saved + back + home + generated); Assert.AreEqual(-1L, project.Generator.Vace.Settings.Seed);
            localization.Load("en"); page.Configure(localization.T); owner.UpdateLayout();
            Assert.AreEqual("not a seed", Find<TextBox>(page, "Seed").Text); Assert.IsFalse(page.TryCommit());
            Find<TextBox>(page, "Seed").Text = "123"; Find<TextBox>(page, "ExportName").Text = "cat-vace";
            Click(page, "Save"); Click(page, "Back"); Click(page, "Home"); Click(page, "Generate");
            Assert.AreEqual(1, saved); Assert.AreEqual(1, back); Assert.AreEqual(1, home); Assert.AreEqual(1, generated); Assert.IsTrue(changed > 0);
            Assert.AreEqual(123L, project.Generator.Vace.Settings.Seed); Assert.AreEqual("cat-vace", project.Generator.Vace.Settings.ExportName);
            Assert.AreEqual(h3, JsonSerializer.Serialize(project.Generator.H3)); Assert.AreEqual(history, JsonSerializer.Serialize(project.Generator.History));
            Assert.AreEqual(preparation, JsonSerializer.Serialize(new { project.World, project.Shots, project.Storyboard }));
        }
        finally { owner.Close(); }
        return Task.CompletedTask;
    });

    private static string AddResult(VideoPreparationProject project, VideoPreparationTests.TestFiles files, bool complete, double seconds = 5.0625)
    {
        var state = VideoVaceWorkspaceState.Ensure(project); var run = new VideoVaceRun { InputHash = new string('a', 64), Complete = complete, GenerationSeconds = 123.5 };
        var path = files.File("synthetic-" + run.Id + ".mp4");
        // This fixture checks UI routing/copy integrity only. It is not a real MP4 or an inference claim.
        File.WriteAllText(path, "Synthetic VACE UI fixture; LOPATA provenance marker; not generated video.");
        var bytes = File.ReadAllBytes(path); var role = "vace-run/" + run.Id + "/" + Guid.NewGuid().ToString("N");
        project.Attachments.Add(new(path, role)); run.Files.Add(new("whole.mp4", role, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes))));
        var lineage = files.File("synthetic-" + run.Id + ".lineage.json"); File.WriteAllText(lineage, "{\"fixture\":true}");
        var lineageBytes = File.ReadAllBytes(lineage); var lineageRole = "vace-run/" + run.Id + "/" + Guid.NewGuid().ToString("N");
        project.Attachments.Add(new(lineage, lineageRole)); run.Files.Add(new("whole.lineage.json", lineageRole, lineageBytes.Length, Convert.ToHexString(SHA256.HashData(lineageBytes))));
        run.Takes.Add(new(Guid.NewGuid().ToString("N"), project.Storyboard!.Frames[0].Id, "whole.mp4", 42, 81, 0, ""));
        run.Results.Add(new(Guid.NewGuid().ToString("N"), role, seconds, DateTimeOffset.UtcNow, [new("wan-vace-1.3b", "VACE fixture", "test", "video")]));
        state.Runs.Add(run); return path;
    }
    [TestMethod]
    public Task OnlyCompletedVaceResultsAppearAndActionsUseSelectedVaceAttachment() => OnUi(() =>
    {
        using var files = new VideoPreparationTests.TestFiles(); var localization = new LocalizationService(); localization.Load("en"); var project = Project();
        var first = AddResult(project, files, true); AddResult(project, files, false); var second = AddResult(project, files, true);
        var h3 = project.Generator!.H3!; var old = new VideoH3Run { InputHash = new string('b', 64) };
        old.Results.Add(new(Guid.NewGuid().ToString("N"), "whole", project.Attachments[0].Role, 5, DateTimeOffset.UtcNow, [])); h3.Runs.Add(old);
        var snapshot = JsonSerializer.Serialize(h3); var page = new VideoVaceWorkspaceControl(localization.T, project) { GenerationAvailable = true };
        string? sent = null; page.SendEditorRequested += path => sent = path; page.ReloadResults(); var owner = Owner(page, true);
        try
        {
            owner.Show(); owner.UpdateLayout(); var list = Find<ListBox>(page, "Timeline"); Assert.AreEqual(2, list.Items.Count);
            Assert.IsTrue(Find<Button>(page, "SendEditor").IsEnabled); Click(page, "SendEditor"); Assert.AreEqual(second, sent);
            Click(page, "PreviousResult"); Click(page, "SendEditor"); Assert.AreEqual(first, sent);
            Assert.AreEqual(project.Generator.Vace!.Runs[0].Results[0].Id, project.Generator.Vace.SelectedResultId);
            Assert.AreEqual(snapshot, JsonSerializer.Serialize(project.Generator.H3));
            Assert.AreEqual(3, project.Generator.Vace.Runs.Count); Assert.AreEqual(3, project.Generator.Vace.Runs.Sum(run => run.Takes.Count));
            StringAssert.Contains(Find<TextBlock>(page, "ResultInfo").Text, "0:02:03");
        }
        finally { owner.Close(); }
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task GenerationPauseStopAndScheduledStateUseIndependentControllerEvents() => OnUi(() =>
    {
        var localization = new LocalizationService(); localization.Load("en"); var page = new VideoVaceWorkspaceControl(localization.T, Project()) { GenerationAvailable = true };
        var generated = 0; var paused = 0; var stopped = 0; var exportStopped = 0;
        page.GenerateRequested += () => generated++; page.PauseGenerationRequested += () => paused++;
        page.CancelGenerationRequested += () => stopped++; page.CancelExportRequested += () => exportStopped++;
        var owner = Owner(page, true);
        try
        {
            owner.Show(); owner.UpdateLayout(); Click(page, "Generate"); Assert.AreEqual(1, generated);
            Assert.AreEqual(Color.FromRgb(0x23, 0x85, 0x49), ((SolidColorBrush)Find<Button>(page, "Generate").Background).Color);
            page.SetGenerating(true); Assert.IsFalse(Find<Button>(page, "Generate").IsEnabled); Assert.IsFalse(page.TryCommit());
            Assert.AreEqual(Color.FromRgb(0x62, 0x69, 0x71), ((SolidColorBrush)Find<Button>(page, "PauseGeneration").Background).Color);
            Assert.AreEqual(Color.FromRgb(0xB7, 0x37, 0x37), ((SolidColorBrush)Find<Button>(page, "CancelGeneration").Background).Color);
            Click(page, "PauseGeneration"); Click(page, "CancelGeneration"); Assert.AreEqual(1, paused); Assert.AreEqual(1, stopped);
            page.SetGenerating(false, pending: true); Assert.AreEqual("▶", Find<Button>(page, "PauseGeneration").Content);
            page.SetGenerating(false); page.SetScheduled(DateTimeOffset.Now.AddMinutes(30));
            Assert.IsFalse(Find<Button>(page, "Generate").IsEnabled); Assert.AreEqual(Visibility.Collapsed, Find<Button>(page, "PauseGeneration").Visibility);
            Click(page, "CancelGeneration"); Assert.AreEqual(2, stopped); page.SetScheduled(null);
            page.SetBusy(true); Click(page, "CancelGeneration"); Assert.AreEqual(1, exportStopped); page.SetBusy(false);
            Assert.IsTrue(Find<Button>(page, "Generate").IsEnabled);
        }
        finally { page.SetGenerating(false); owner.Close(); }
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task ProgressUsesVacePassesAndNeverCompletesAtTheLastSamplingStep() => OnUi(() =>
    {
        var localization = new LocalizationService(); localization.Load("en"); var page = new VideoVaceWorkspaceControl(localization.T, Project());
        try
        {
            page.SetGenerating(true); page.GenerationProgress("[VACE event] {\"kind\":\"elapsed\",\"elapsed_seconds\":30}");
            page.GenerationProgress("[VACE event] {\"kind\":\"pass\",\"index\":2,\"total\":8,\"item_index\":2,\"item_total\":8}");
            page.GenerationProgress("[VACE event] {\"kind\":\"stage\",\"text\":\"sampling\"}");
            page.GenerationProgress("[VACE event] {\"kind\":\"progress\",\"value\":1,\"total\":20}");
            page.GenerationProgress("[VACE event] {\"kind\":\"progress\",\"value\":2,\"total\":20}");
            StringAssert.Contains(Find<TextBlock>(page, "ProgressSummary").Text, "2/8"); StringAssert.Contains(Find<TextBlock>(page, "ProgressSummary").Text, "2/20");
            StringAssert.Contains(Find<TextBlock>(page, "ProgressSummary").Text, localization.T(Keys + "ProgressRemainingSampling"));
            page.GenerationProgress("[VACE event] {\"kind\":\"stage\",\"text\":\"decoding\"}");
            page.GenerationProgress("[VACE event] {\"kind\":\"progress\",\"value\":9,\"total\":9}");
            Assert.IsFalse(Find<TextBlock>(page, "ProgressSummary").Text.Contains("9/9", StringComparison.Ordinal));
            var before = Find<TextBlock>(page, "ProgressSummary").Text; page.GenerationProgress("[H3 event] {\"kind\":\"done\",\"complete\":true}");
            Assert.AreEqual(before, Find<TextBlock>(page, "ProgressSummary").Text);
            page.GenerationProgress("[VACE event] {\"kind\":\"stage\",\"text\":\"assembling\"}"); Assert.IsTrue(Find<H3FluidProgress>(page, "Progress").Value < 100);
            page.GenerationProgress("[VACE event] {\"kind\":\"done\",\"complete\":true}"); Assert.AreEqual(100d, Find<H3FluidProgress>(page, "Progress").Value);
            page.GenerationProgress("[VACE event] {\"kind\":\"progress\",\"value\":\"invalid\"}");
            page.GenerationProgress("[VACE event] malformed"); Assert.AreEqual(100d, Find<H3FluidProgress>(page, "Progress").Value);
        }
        finally { page.SetGenerating(false); }
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task RestoredElapsedTimeIsSeededOnceThenOnlyMonotonicClockTicksAdvanceIt() => OnUi(async () =>
    {
        var localization = new LocalizationService(); localization.Load("en"); var page = new VideoVaceWorkspaceControl(localization.T, Project());
        var flags = BindingFlags.NonPublic | BindingFlags.Instance; var tick = typeof(VideoVaceWorkspaceControl).GetMethod("ProgressTick", flags)!;
        var clock = (Stopwatch)typeof(VideoVaceWorkspaceControl).GetField("_activeClock", flags)!.GetValue(page)!;
        void Tick() => tick.Invoke(page, [null, EventArgs.Empty]);
        void Observe(double value) => page.GenerationProgress("[VACE event] " + JsonSerializer.Serialize(new { kind = "elapsed", elapsed_seconds = value }));
        string Shown() => Find<TextBlock>(page, "ProgressSummary").Text.Split(localization.T(Keys + "ProgressElapsed") + " ", StringSplitOptions.None)[1].Split(" · ", StringSplitOptions.None)[0];
        try
        {
            page.SetGenerating(true); Observe(1380); Tick(); Assert.AreEqual("0:23:00", Shown());
            Observe(999999); page.GenerationProgress("[VACE event] {\"kind\":\"gpu\",\"utilization\":80}"); Tick(); Assert.AreEqual("0:23:00", Shown());
            await Task.Delay(1100); Tick(); Assert.AreEqual("0:23:01", Shown());
            page.SetGenerating(false, pending: true); var paused = clock.Elapsed; Observe(999999); await Task.Delay(30); Assert.AreEqual(paused, clock.Elapsed);
            page.SetGenerating(true); Observe(999999); Tick(); Assert.AreEqual("0:23:01", Shown());
        }
        finally { page.SetGenerating(false); }
    });

    [TestMethod]
    public Task ScheduleDialogCanEmitDelayWithoutChangingOtherGeneratorState() => OnUi(() =>
    {
        var localization = new LocalizationService(); localization.Load("ru"); var project = Project(); var snapshot = JsonSerializer.Serialize(project.Generator!.H3);
        var page = new VideoVaceWorkspaceControl(localization.T, project) { GenerationAvailable = true }; var owner = Owner(page, true); DateTimeOffset? selected = null;
        page.ScheduleGenerationRequested += value => selected = value;
        try
        {
            owner.Show(); owner.UpdateLayout(); var button = Find<Button>(page, "Generate"); Assert.AreEqual(2, button.ContextMenu!.Items.Count);
            var before = DateTimeOffset.Now;
            Ui.Value.BeginInvoke(() =>
            {
                var dialog = owner.OwnedWindows.Cast<Window>().Single(window => AutomationProperties.GetAutomationId(window) == "VideoVace.Schedule.Window");
                var delay = ScenarioNavigationTests.LogicalDescendants(dialog).OfType<TextBox>().Single(input => AutomationProperties.GetAutomationId(input) == "VideoVace.Schedule.Delay"); delay.Text = "30";
                dialog.UpdateLayout(); Save(dialog, "schedule-delay");
                ScenarioNavigationTests.LogicalDescendants(dialog).OfType<Button>().Single(apply => AutomationProperties.GetAutomationId(apply) == "VideoVace.Schedule.Apply").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            ((MenuItem)button.ContextMenu.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.IsNotNull(selected); Assert.IsTrue(selected > before.AddMinutes(29) && selected < DateTimeOffset.Now.AddMinutes(31));
            Assert.AreEqual(snapshot, JsonSerializer.Serialize(project.Generator.H3));
        }
        finally { owner.Close(); }
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task ExactExportPreservesBytesAndRejectsModifiedSourcesOrCancellationWithoutReplacingTarget() => OnUi(async () =>
    {
        using var files = new VideoPreparationTests.TestFiles(); var localization = new LocalizationService(); localization.Load("en"); var project = Project();
        var source = AddResult(project, files, true); var original = File.ReadAllBytes(source); var h3 = JsonSerializer.Serialize(project.Generator!.H3);
        var page = new VideoVaceWorkspaceControl(localization.T, project); var target = files.File("exported.mp4");
        Assert.IsTrue(await page.ExportVideoToAsync(target)); CollectionAssert.AreEqual(original, File.ReadAllBytes(target));
        // Changing new-result settings never recodes or changes an already completed MP4.
        var output = Find<ComboBox>(page, "OutputHeight"); output.SelectedIndex = output.Items.Count - 1;
        Assert.IsTrue(await page.ExportVideoToAsync(target)); CollectionAssert.AreEqual(original, File.ReadAllBytes(target));
        using (var cancel = new CancellationTokenSource()) { cancel.Cancel(); Assert.IsFalse(await page.ExportVideoToAsync(target, cancel.Token)); }
        CollectionAssert.AreEqual(original, File.ReadAllBytes(target));
        File.AppendAllText(source, " altered"); Assert.IsFalse(await page.ExportVideoToAsync(target)); CollectionAssert.AreEqual(original, File.ReadAllBytes(target));
        Assert.IsFalse(Directory.EnumerateFiles(Path.GetDirectoryName(target)!, "*.partial").Any());
        Assert.AreEqual(h3, JsonSerializer.Serialize(project.Generator.H3)); Assert.IsTrue(File.Exists(source));
    });

    [TestMethod]
    public Task ExistingNativeVaceArchivePlaysWholeVideoAndExportsExactVerifiedCopy() => OnUi(async () =>
    {
        var archive = Environment.GetEnvironmentVariable("LOPATA_VACE_WORKSPACE_PROJECT");
        if (string.IsNullOrWhiteSpace(archive)) { Assert.Inconclusive("An explicit independently generated VACE archive is required; this test never starts a model."); return; }
        Assert.IsTrue(File.Exists(archive), "The explicitly selected VACE archive must exist.");
        var originalArchiveHash = SHA256.HashData(await File.ReadAllBytesAsync(archive));
        using var files = new VideoPreparationTests.TestFiles();
        var copy = files.File("test-copy.lopataAIVid"); File.Copy(archive, copy);
        var project = VideoPreparationArchive.Load(copy, files.File("opened-copy")); project.Validate();
        var results = project.Generator?.Vace?.Runs.Where(run => run.Complete).SelectMany(run => run.Results).ToArray();
        Assert.IsNotNull(results); Assert.AreEqual(1, results.Length, "The supplied smoke archive must contain one completed whole VACE result.");
        var result = results[0]; var source = project.Attachments.Single(attachment => attachment.Role == result.VideoRole).Path;
        Assert.IsTrue(Path.GetFullPath(source).StartsWith(Path.GetFullPath(files.Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Playback must use the extracted test copy.");
        var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(source));
        var localization = new LocalizationService(); localization.Load("ru"); var page = new VideoVaceWorkspaceControl(localization.T, project);
        var player = Find<MediaElement>(page, "Player"); var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.MediaOpened += (_, _) => opened.TrySetResult(); player.MediaFailed += (_, error) => opened.TrySetException(error.ErrorException);
        var owner = Owner(page, true, 1400, 850);
        try
        {
            owner.Show();
            if (!player.NaturalDuration.HasTimeSpan) await opened.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.IsTrue(player.NaturalDuration.HasTimeSpan); Assert.IsTrue(player.NaturalDuration.TimeSpan.TotalSeconds > 0);
            Assert.IsTrue(player.NaturalVideoWidth > 0 && player.NaturalVideoHeight > 0);
            Assert.IsTrue(Find<Button>(page, "PlayPreview").IsEnabled); Assert.IsTrue(Find<Button>(page, "ExportVideo").IsEnabled);
            Assert.AreEqual(1, Find<ListBox>(page, "Timeline").Items.Count);
            StringAssert.Contains(Find<ListBox>(page, "Timeline").Items[0].ToString()!, localization.T(Keys + "WholeVideo"));
            await AIHub.Services.VideoEditor.VideoRuntime.PrepareAsync(default);
            using var probe = await AIHub.Services.VideoEditor.VideoRuntime.ProbeAsync(source, default);
            var video = probe.RootElement.GetProperty("streams").EnumerateArray().Single(stream => stream.GetProperty("codec_type").GetString() == "video");
            Assert.AreEqual("16/1", video.GetProperty("avg_frame_rate").GetString());
            var frameCount = int.Parse(video.GetProperty("nb_frames").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            var nativeDuration = double.Parse(video.GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            Assert.AreEqual(result.Seconds, frameCount / 16d, 0.000001); Assert.AreEqual(result.Seconds, nativeDuration, 0.000001);
            var reportedDuration = player.NaturalDuration.TimeSpan.TotalSeconds;
            TaskCompletionSource? ended = null; player.MediaEnded += (_, _) => ended?.TrySetResult();
            var samples = new List<double>(); var watch = new Stopwatch();
            var sample = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(20) };
            sample.Tick += (_, _) => samples.Add(player.Position.TotalSeconds);
            bool fullEnded; double fullWallSeconds;
            try
            {
                ended = new(TaskCreationOptions.RunContinuationsAsynchronously); player.Position = TimeSpan.Zero;
                sample.Start(); watch.Start(); Click(page, "PlayPreview");
                fullEnded = await Task.WhenAny(ended.Task, Task.Delay(TimeSpan.FromSeconds(Math.Min(30, nativeDuration + 3)))) == ended.Task;
                watch.Stop(); fullWallSeconds = watch.Elapsed.TotalSeconds;
            }
            finally { sample.Stop(); if (!watch.IsRunning) player.Pause(); }
            var maximumPosition = samples.DefaultIfEmpty(0).Max();
            // The workspace resets Position on MediaEnded. Observe the final frame
            // before that reset, then explicitly seek to it with scrubbing enabled.
            var lastFrameStart = (frameCount - 1) / 16d;
            player.Position = TimeSpan.FromSeconds(lastFrameStart); player.Pause(); await Task.Delay(150);
            var soughtPosition = player.Position.TotalSeconds; owner.UpdateLayout(); Save(page, "real-result");
            ended = new(TaskCreationOptions.RunContinuationsAsynchronously); var tailWatch = Stopwatch.StartNew();
            player.Play(); var tailEnded = await Task.WhenAny(ended.Task, Task.Delay(TimeSpan.FromSeconds(5))) == ended.Task; tailWatch.Stop(); player.Pause();
            Console.WriteLine("VACE_PLAYBACK_EVIDENCE " + JsonSerializer.Serialize(new { NativeDuration = nativeDuration, FrameCount = frameCount,
                WpfNaturalDuration = reportedDuration, FullEnded = fullEnded, FullWallSeconds = fullWallSeconds, MaximumPosition = maximumPosition,
                LastFrameStart = lastFrameStart, SoughtPosition = soughtPosition, TailEnded = tailEnded, TailWallSeconds = tailWatch.Elapsed.TotalSeconds, Samples = samples }));
            Assert.IsTrue(fullEnded, "Playback must raise MediaEnded for the complete existing file.");
            Assert.IsTrue(tailEnded, "Playback from the final frame must also reach MediaEnded.");
            Assert.AreEqual(lastFrameStart, soughtPosition, 1d / 16, "The final native frame must remain seekable.");
            Assert.IsTrue(maximumPosition >= lastFrameStart - 1d / 16, "Observe playback reaching the last native frame before accepting duration rounding.");
            // This Windows media backend reports 2 s for the independently verified
            // 41/16 s file. Full playback and the final-frame seek above demonstrate
            // that only the reported duration is rounded, not the media content.
            Assert.IsTrue(reportedDuration <= nativeDuration && nativeDuration - reportedDuration < 1,
                "WPF may report a duration rounded down to whole seconds; native duration, full playback and final-frame access remain exact checks above.");
            var exported = files.File("verified-export.mp4"); Assert.IsTrue(await page.ExportVideoToAsync(exported));
            CollectionAssert.AreEqual(sourceHash, SHA256.HashData(await File.ReadAllBytesAsync(exported)));
            CollectionAssert.AreEqual(sourceHash, SHA256.HashData(await File.ReadAllBytesAsync(source)));
            CollectionAssert.AreEqual(originalArchiveHash, SHA256.HashData(await File.ReadAllBytesAsync(archive)), "The source smoke archive must remain unchanged.");
        }
        finally { player.Close(); owner.Close(); }
    });

    private static void Save(FrameworkElement page, string name)
    {
        var folder = Environment.GetEnvironmentVariable("LOPATA_VACE_WORKSPACE_IMAGES"); if (folder is null) return;
        page.UpdateLayout(); var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)page.ActualWidth, (int)page.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(page);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(folder); using var output = File.Create(Path.Combine(folder, name + ".png")); encoder.Save(output);
    }
}
