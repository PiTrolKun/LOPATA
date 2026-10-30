using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VideoPreparation;
using Button = System.Windows.Controls.Button;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoVacePreparationUiTests
{
    private static readonly Lazy<Dispatcher> Ui = new(() =>
    {
        var ready = new TaskCompletionSource<Dispatcher>();
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            ready.SetResult(dispatcher); Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return ready.Task.GetAwaiter().GetResult();
    });
    private static Task OnUi(Func<Task> work) => Ui.Value.InvokeAsync(work).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(30));
    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject => ScenarioNavigationTests.LogicalDescendants(root)
        .OfType<T>().Single(e => AutomationProperties.GetAutomationId(e) == id);
    private static async Task Until(Func<bool> condition)
    { for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10); Assert.IsTrue(condition()); }

    private sealed class Transfer
    {
        internal int Checks, Downloads, Active, Cancelled;
        internal bool Complete, DelayCancellation;
        internal readonly TaskCompletionSource Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource CancellationCleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal IVideoVacePreparation Create() => new Fake(this);
        private sealed class Fake(Transfer transfer) : IVideoVacePreparation
        {
            public IReadOnlyList<ManagedModelArtifactCard> Cards { get; } = [new()
            {
                ModelArtifactId = "vace-fixture", DisplayName = "WAN VACE 1.3B fixture", Revision = new string('a', 40),
                Files = [new() { RelativePath = "model.safetensors", SizeBytes = 100 }, new() { RelativePath = "encoder.safetensors", SizeBytes = 200 }]
            }];
            public int ParallelConnections { get; set; }
            public IReadOnlyList<VideoH3FilePresence> Inspect() =>
                [new("vace-fixture", "model.safetensors", 100, true), new("vace-fixture", "encoder.safetensors", transfer.Complete ? 200 : 40, transfer.Complete)];
            public async Task<bool> PrepareAsync(bool download, IProgress<ManagedModelDownloadProgress>? progress, CancellationToken token)
            {
                if (!download) { transfer.Checks++; return transfer.Complete; }
                transfer.Downloads++; transfer.Active++;
                try
                {
                    progress?.Report(new("vace-fixture", "encoder.safetensors", 180, 300, 3 * 1024 * 1024, "downloading"));
                    await transfer.Finish.Task.WaitAsync(token); transfer.Complete = true;
                    progress?.Report(new("vace-fixture", "", 300, 300, 0, "verified")); return true;
                }
                catch (OperationCanceledException)
                {
                    transfer.Cancelled++;
                    if (transfer.DelayCancellation) await transfer.CancellationCleanup.Task;
                    throw;
                }
                finally { transfer.Active--; }
            }
            public void Dispose() { }
        }
    }
    private static Window Owner(FrameworkElement content, bool dark, double width = 1120, double height = 760)
    {
        var owner = new Window { Content = content, Width = width, Height = height, UseLayoutRounding = true,
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI") };
        owner.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/AIHub;component/Controls/VideoPreparationResources.xaml", UriKind.Relative) });
        owner.Resources["TextPrimaryBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#F8FAFC" : "#1F1F1F"));
        owner.Resources["TextSecondaryBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#AAB4C4" : "#5D6470"));
        owner.Resources["WindowBackgroundBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#111827" : "#F3F3F3"));
        owner.Resources["PanelBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#172033" : "#FFFFFF"));
        owner.Resources["SecondaryButtonBackgroundBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#111827" : "#F8F8F8"));
        owner.Resources["LineBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#2D374B" : "#DADDE3"));
        owner.Resources["AccentBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2563EB"));
        owner.Resources["UiBodyFontSize"] = 14d; owner.Resources["UiSectionFontSize"] = 18d;
        return owner;
    }
    private static void AssertNoRuntimeOrExternalAccessActions(DependencyObject page)
    {
        Assert.IsFalse(ScenarioNavigationTests.LogicalDescendants(page).OfType<PasswordBox>().Any());
        Assert.IsFalse(ScenarioNavigationTests.LogicalDescendants(page).OfType<Button>().Any(button =>
        {
            var id = AutomationProperties.GetAutomationId(button);
            if (id == "VideoVace.Next") return button.Visibility != Visibility.Collapsed;
            return id.Contains("Next", StringComparison.Ordinal) || id.Contains("Generate", StringComparison.Ordinal)
                || id.Contains("Token", StringComparison.Ordinal) || id.Contains("OpenModel", StringComparison.Ordinal);
        }));
    }

    [TestMethod, DataRow("ru", true), DataRow("en", false)]
    public Task InlinePageOnlyChecksOnLoadAndShowsDownloadProgressThenFileReadiness(string language, bool dark) => OnUi(async () =>
    {
        var localization = new LocalizationService(); localization.Load(language); var transfer = new Transfer();
        var page = new VideoVacePreparationControl(localization.T, "fixture storage", 4, transfer.Create);
        var owner = Owner(page, dark);
        try
        {
            owner.Show(); await Until(() => transfer.Checks == 1 && !page.Busy);
            Assert.AreEqual(0, transfer.Downloads); Assert.IsFalse(page.Ready); Assert.AreEqual(0, owner.OwnedWindows.Count);
            StringAssert.Contains(Find<TextBlock>(page, "VideoVace.File.vace-fixture.encoder.safetensors").Text, localization.T("VideoPrep.VaceFile.partial"));
            AssertNoRuntimeOrExternalAccessActions(page);
            page.Configure(localization.T); Assert.AreEqual(1, transfer.Checks);
            Find<Button>(page, "VideoVace.Download").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => transfer.Downloads == 1 && Find<TextBlock>(page, "VideoVace.Speed").Text.Contains("3", StringComparison.Ordinal));
            StringAssert.Contains(Find<TextBlock>(page, "VideoVace.File.vace-fixture.encoder.safetensors").Text, localization.T("VideoPrep.VaceFile.downloading"));
            Assert.IsFalse(Find<Button>(page, "VideoVace.Download").IsEnabled); Assert.IsFalse(Find<Button>(page, "VideoVace.Verify").IsEnabled);
            Assert.IsTrue(Find<Button>(page, "VideoVace.Cancel").IsEnabled);
            Save(page, language + "-downloading");
            Find<Button>(page, "VideoVace.Cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => !page.Busy); Assert.IsFalse(page.Ready); Assert.AreEqual(1, transfer.Cancelled);
            transfer.Finish.TrySetResult(); await page.RunAsync(true);
            Assert.IsTrue(page.Ready); Assert.AreEqual(localization.T("VideoPrep.VaceFilesReady"), Find<TextBlock>(page, "VideoVace.Status").Text);
            Assert.AreEqual(100d, Find<System.Windows.Controls.ProgressBar>(page, "VideoVace.Progress").Value);
            Assert.IsFalse(Find<Button>(page, "VideoVace.Download").IsEnabled); Assert.IsTrue(Find<Button>(page, "VideoVace.Verify").IsEnabled);
            AssertNoRuntimeOrExternalAccessActions(page); Assert.IsTrue(page.CanCheckpoint());
            Assert.IsFalse(ScenarioNavigationTests.LogicalDescendants(page).OfType<TextBlock>().Any(t => t.Text.StartsWith("VideoPrep.", StringComparison.Ordinal)));
            Save(page, language + "-ready");
        }
        finally { owner.Close(); }
    });

    [TestMethod, DataRow(false), DataRow(true)]
    public Task NavigationCancelsDownloadBeforeLeavingAndDoesNotDoubleNavigate(bool home) => OnUi(async () =>
    {
        var localization = new LocalizationService(); localization.Load("ru"); var transfer = new Transfer { DelayCancellation = true };
        var page = new VideoVacePreparationControl(localization.T, "fixture", 4, transfer.Create); var owner = Owner(page, true);
        var backs = 0; var homes = 0;
        page.BackRequested += () => { Assert.IsFalse(page.Busy); Assert.AreEqual(0, transfer.Active); backs++; };
        page.HomeRequested += () => { Assert.IsFalse(page.Busy); Assert.AreEqual(0, transfer.Active); homes++; };
        try
        {
            owner.Show(); await Until(() => transfer.Checks == 1 && !page.Busy);
            var task = page.RunAsync(true); await Until(() => transfer.Active == 1);
            var button = Find<Button>(page, home ? "VideoVace.Home" : "VideoVace.Back");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => transfer.Cancelled == 1);
            Assert.IsTrue(page.Busy); Assert.AreEqual(1, transfer.Active); Assert.AreEqual(0, backs + homes);
            transfer.CancellationCleanup.TrySetResult(); await task;
            Assert.AreEqual(home ? 0 : 1, backs); Assert.AreEqual(home ? 1 : 0, homes); Assert.AreEqual(1, transfer.Cancelled);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); page.RequestExit();
            Assert.AreEqual(1, backs + homes);
            owner.Content = null; await Until(() => !page.IsLoaded);
            owner.Content = page; await Until(() => page.IsLoaded); page.RequestBack();
            Assert.AreEqual(2, backs + homes); Assert.AreEqual(1, transfer.Checks);
        }
        finally { transfer.CancellationCleanup.TrySetResult(); owner.Close(); }
    });

    [TestMethod]
    public Task UnloadingCancelsDownloadWithoutNavigatingOrClaimingReadiness() => OnUi(async () =>
    {
        var transfer = new Transfer(); var page = new VideoVacePreparationControl(k => k, "fixture", 4, transfer.Create);
        var owner = Owner(page, false); var navigation = 0; page.BackRequested += () => navigation++; page.HomeRequested += () => navigation++;
        try
        {
            owner.Show(); await Until(() => transfer.Checks == 1 && !page.Busy);
            var task = page.RunAsync(true); await Until(() => transfer.Active == 1);
            owner.Content = null; await task;
            Assert.IsFalse(page.Busy); Assert.IsFalse(page.Ready); Assert.AreEqual(1, transfer.Cancelled); Assert.AreEqual(0, navigation);
        }
        finally { owner.Close(); }
    });

    [TestMethod]
    public Task ReadyPageEntersWorkspaceOnlyOnNextAndBackReusesTheSameProjectWhileHomeNavigatesOnce() => OnUi(async () =>
    {
        var localization = new LocalizationService(); localization.Load("ru"); var transfer = new Transfer { Complete = true };
        var project = VideoPreparationFinishTests.ReadyProject();
        var h3 = VideoH3WorkspaceState.Ensure(project); h3.Settings.ExportName = "existing-h3"; h3.Settings.Seed = 123;
        var previous = System.Text.Json.JsonSerializer.Serialize(h3); var created = 0; var homes = 0; var backs = 0;
        var page = new VideoVacePreparationControl(localization.T, "fixture", 4, transfer.Create);
        VideoVaceWorkspaceControl? workspace = null;
        page.CreateWorkspace = () => { created++; return workspace = new(localization.T, project) { GenerationAvailable = true }; };
        page.HomeRequested += () => homes++; page.BackRequested += () => backs++;
        var owner = Owner(page, true);
        try
        {
            Assert.AreEqual(Visibility.Collapsed, Find<Button>(page, "VideoVace.Next").Visibility);
            owner.Show(); await Until(() => transfer.Checks == 1 && !page.Busy);
            Assert.IsTrue(page.Ready); Assert.AreEqual(0, created); Assert.IsNull(workspace);
            Assert.AreEqual(Visibility.Visible, Find<Button>(page, "VideoVace.Next").Visibility);
            Assert.AreEqual(0, owner.OwnedWindows.Count); Assert.IsTrue(page.CanCheckpoint());
            Find<Button>(page, "VideoVace.Next").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.AreEqual(1, created); Assert.AreSame(workspace, page.Content);
            Assert.AreEqual(0, transfer.Downloads); Assert.AreEqual(0, backs + homes); Assert.AreEqual(0, owner.OwnedWindows.Count);
            Find<Button>(workspace!, "VideoVace.Workspace.Back").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.AreNotSame(workspace, page.Content); Assert.AreEqual(0, backs + homes);
            Assert.AreEqual(Visibility.Visible, Find<Button>(page, "VideoVace.Next").Visibility);
            Assert.AreEqual(previous, System.Text.Json.JsonSerializer.Serialize(project.Generator!.H3));
            Find<Button>(page, "VideoVace.Next").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.AreSame(workspace, page.Content); Assert.AreEqual(1, created); Assert.AreEqual(1, transfer.Checks);
            Find<Button>(workspace!, "VideoVace.Workspace.Home").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Find<Button>(workspace!, "VideoVace.Workspace.Home").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.AreEqual(1, homes); Assert.AreEqual(0, backs);
            Assert.AreEqual(previous, System.Text.Json.JsonSerializer.Serialize(project.Generator.H3));
        }
        finally { owner.Close(); }
    });

    private sealed class ManyFiles : IVideoVacePreparation
    {
        public IReadOnlyList<ManagedModelArtifactCard> Cards { get; } = [new()
        {
            ModelArtifactId = "vace-layout", DisplayName = "WAN VACE 1.3B layout fixture", Revision = new string('b', 40),
            Files = Enumerable.Range(1, 14).Select(i => new ManagedModelArtifactFile
            { RelativePath = "models/long-file-name-component-" + i + ".safetensors", SizeBytes = 1024 * 1024 }).ToList()
        }];
        public int ParallelConnections { get; set; }
        public IReadOnlyList<VideoH3FilePresence> Inspect() => Cards.SelectMany(card => card.Files.Select(file =>
            new VideoH3FilePresence(card.ModelArtifactId, file.RelativePath, 0, false))).ToArray();
        public Task<bool> PrepareAsync(bool download, IProgress<ManagedModelDownloadProgress>? progress, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Assert.IsFalse(download); return Task.FromResult(false); }
        public void Dispose() { }
    }
    [TestMethod, DataRow("ru", true), DataRow("en", false)]
    public Task RealThemeKeepsAllActionsVisibleAndFileListScrollableAt900By650(string language, bool dark) => OnUi(async () =>
    {
        var localization = new LocalizationService(); localization.Load(language);
        var page = new VideoVacePreparationControl(localization.T, "fixture", 4, () => new ManyFiles()); var owner = Owner(page, dark, 900, 650);
        try
        {
            owner.Show(); await Until(() => !page.Busy); page.UpdateLayout();
            var scroll = Find<ScrollViewer>(page, "VideoVace.Files"); Assert.IsTrue(scroll.ViewportHeight > 0); Assert.IsTrue(scroll.ExtentHeight > scroll.ViewportHeight);
            var download = Find<Button>(page, "VideoVace.Download"); Assert.AreSame(owner.FindResource(typeof(Button)), download.Style);
            Assert.AreEqual(((SolidColorBrush)owner.Resources["SecondaryButtonBackgroundBrush"]).Color, ((SolidColorBrush)download.Background).Color);
            foreach (var id in new[] { "VideoVace.Back", "VideoVace.Home", "VideoVace.Verify", "VideoVace.Download", "VideoVace.Cancel" })
            {
                var button = Find<Button>(page, id); var position = button.TransformToAncestor(page).Transform(new Point());
                Assert.IsTrue(position.X >= 0 && position.X + button.ActualWidth <= page.ActualWidth + 1, id + " overflows horizontally");
                Assert.IsTrue(position.Y >= 0 && position.Y + button.ActualHeight <= page.ActualHeight + 1, id + " is outside the page");
            }
            Save(page, language + "-900x650-top"); scroll.ScrollToEnd(); page.UpdateLayout(); Assert.IsTrue(scroll.VerticalOffset > 0);
            Save(page, language + "-900x650-bottom");
        }
        finally { owner.Close(); }
    });

    private sealed class CatalogOnly(string root) : IVideoVacePreparation
    {
        public IReadOnlyList<ManagedModelArtifactCard> Cards { get; } = VideoVaceCatalog.Cards(root);
        public int ParallelConnections { get; set; }
        public IReadOnlyList<VideoH3FilePresence> Inspect() => Cards.SelectMany(card => card.Files.Select(file =>
            new VideoH3FilePresence(card.ModelArtifactId, file.RelativePath, 0, false))).ToArray();
        public Task<bool> PrepareAsync(bool download, IProgress<ManagedModelDownloadProgress>? progress, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Assert.IsFalse(download, "A catalog presentation test must not download weights."); return Task.FromResult(false); }
        public void Dispose() { }
    }
    [TestMethod]
    public Task ActualCatalogShowsAllThreePinnedFilesWithoutNetworkOrDownloads() => OnUi(async () =>
    {
        using var files = new VideoPreparationTests.TestFiles(); var localization = new LocalizationService(); localization.Load("ru");
        var modelRoot = files.File("models"); var page = new VideoVacePreparationControl(localization.T, modelRoot, 4, () => new CatalogOnly(modelRoot));
        var owner = Owner(page, true, 900, 650);
        try
        {
            owner.Show(); await Until(() => !page.Busy); page.UpdateLayout(); Assert.IsFalse(page.Ready);
            var expected = VideoVaceCatalog.Cards(modelRoot);
            Assert.AreEqual(3, expected.SelectMany(card => card.Files).Count());
            Assert.AreEqual(3, ScenarioNavigationTests.LogicalDescendants(page).OfType<TextBlock>()
                .Count(t => AutomationProperties.GetAutomationId(t).StartsWith("VideoVace.File.", StringComparison.Ordinal)));
            foreach (var card in expected)
                foreach (var file in card.Files)
                    StringAssert.Contains(Find<TextBlock>(page, "VideoVace.Size." + card.ModelArtifactId + "." + file.RelativePath).Text,
                        ComponentCardViewModel.FormatBytes(file.SizeBytes));
            AssertNoRuntimeOrExternalAccessActions(page); Save(page, "ru-real-catalog");
        }
        finally { owner.Close(); }
    });

    [TestMethod]
    public Task RetiredVaceRequiresNewChoiceAndCancelPreservesSavedProjectAndSettings() => OnUi(async () =>
    {
        using var files = new VideoPreparationTests.TestFiles(); var localization = new LocalizationService(); localization.Load("ru");
        var project = VideoPreparationFinishTests.ReadyProject(); project.CurrentStep = "finish";
        var h3Choice = VideoGeneratorPlan.SaveChoice(project, new("minimax-h3", 1280, 720, 24, 42, "earlier H3 settings"));
        var h3 = VideoH3WorkspaceState.Ensure(project); h3.Settings.ExportName = "previous-h3-result"; h3.Settings.Steps = 24;
        var selected = VideoGeneratorPlan.SaveChoice(project, new("wan-vace-1.3b", 1280, 720, 24, -1, "saved settings"));
        var path = files.File("p.lopataAIVid"); VideoPreparationArchive.Save(project, path);
        var session = new VideoPreparationSession(files.File("recovery")); session.Load(path);
        var h3Snapshot = System.Text.Json.JsonSerializer.Serialize(session.Project.Generator!.H3);
        var host = new VideoPreparationControl(localization.T, session); var transfer = new Transfer();
        var routes = 0;
        host.ConfigureGeneratorPreparation(_ => routes++);
        var owner = Owner(host, true);
        try
        {
            owner.Show();
            _ = owner.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                owner.OwnedWindows.OfType<VideoGeneratorChoiceWindow>().Single().Close()));
            Assert.IsFalse(host.Next());
            Assert.AreEqual(0, routes); Assert.AreEqual(0, transfer.Checks); Assert.AreEqual(0, transfer.Downloads);
            Assert.IsFalse(VideoGeneratorPlan.CanResume(session.Project)); Assert.AreEqual(0, owner.OwnedWindows.Count);
            Assert.AreEqual(project.Id, session.Project.Id); Assert.AreEqual("finish", session.Project.CurrentStep);
            Assert.AreEqual(selected.Id, VideoGeneratorPlan.Selected(session.Project)!.Id);
            Assert.AreEqual("saved settings", VideoGeneratorPlan.Selected(session.Project)!.Settings.Notes);
            Assert.AreEqual(2, session.Project.Generator!.History.Count);
            Assert.AreEqual(h3Choice.Settings, session.Project.Generator.History.Single(c => c.Id == h3Choice.Id).Settings);
            Assert.AreEqual(h3Snapshot, System.Text.Json.JsonSerializer.Serialize(session.Project.Generator.H3));
            await Task.CompletedTask;
        }
        finally { owner.Close(); }
    });

    private static void Save(FrameworkElement page, string name)
    {
        var folder = Environment.GetEnvironmentVariable("LOPATA_VACE_PAGE_IMAGES"); if (folder is null) return;
        page.UpdateLayout(); var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)page.ActualWidth, (int)page.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(page); var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        System.IO.Directory.CreateDirectory(folder); using var output = System.IO.File.Create(System.IO.Path.Combine(folder, name + ".png")); encoder.Save(output);
    }
}
