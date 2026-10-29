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
public sealed class VideoH3PageTests
{
    private static ManagedModelArtifactCard Card(string id = "bundle") => new()
    { ModelArtifactId = id, DisplayName = id, Revision = "1234567890", Files = [new() { RelativePath = "a.bin", SizeBytes = 100 }, new() { RelativePath = "b.bin", SizeBytes = 200 }] };
    [TestMethod]
    public void PerFileProgressUsesCardPrefixAndPresenceDoesNotPretendVerification()
    {
        var s = new VideoH3DownloadState([Card()]);
        s.Refresh([new("bundle", "a.bin", 100, true), new("bundle", "b.bin", 40, false)]);
        Assert.AreEqual("downloaded", s.Rows[0].Stage); Assert.AreEqual("partial", s.Rows[1].Stage);
        s.Apply(new("bundle", "b.bin", 150, 300, 4096, "downloading"));
        Assert.AreEqual("downloading", s.Rows[1].Stage); Assert.AreEqual(50L, s.Rows[1].Stored); Assert.AreEqual(150L, s.StoredBytes); Assert.AreEqual(4096d, s.BytesPerSecond);
        s.Apply(new("bundle", "b.bin", 300, 300, 0, "assembling")); Assert.AreEqual(0d, s.BytesPerSecond); Assert.AreEqual("assembling", s.Rows[1].Stage);
        s.Apply(new("bundle", "b.bin", 100, 300, 0, "verifying")); Assert.AreEqual("ready", s.Rows[0].Stage); Assert.AreEqual("verifying", s.Rows[1].Stage);
        s.Apply(new("bundle", "", 300, 300, 0, "verified")); Assert.IsTrue(s.Rows.All(r => r.Stage == "ready"));
    }
    [TestMethod]
    public void CancelAndErrorRetainPartialBytesAndVerifiedOtherFiles()
    {
        var files = new VideoH3FilePresence[] { new("bundle", "a.bin", 100, true), new("bundle", "b.bin", 70, false) };
        var s = new VideoH3DownloadState([Card()]); s.Refresh(files);
        s.Apply(new("bundle", "b.bin", 100, 300, 0, "verifying")); s.Fail(files);
        Assert.AreEqual("ready", s.Rows[0].Stage); Assert.AreEqual("failed", s.Rows[1].Stage); Assert.AreEqual(70L, s.Rows[1].Stored);
        s.Refresh(files); Assert.AreEqual("partial", s.Rows[1].Stage); Assert.AreEqual(170L, s.StoredBytes);
    }
    private static readonly Lazy<Dispatcher> Ui = new(() =>
    {
        var ready = new TaskCompletionSource<Dispatcher>(); var thread = new Thread(() =>
        { var d = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(d)); ready.SetResult(d); Dispatcher.Run(); }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return ready.Task.GetAwaiter().GetResult();
    });
    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject => ScenarioNavigationTests.LogicalDescendants(root)
        .OfType<T>().Single(e => AutomationProperties.GetAutomationId(e) == id);
    private sealed class Transfer
    {
        internal int Checks, Downloads;
        internal bool Complete;
        internal TaskCompletionSource Finish = new();
        internal IVideoH3Preparation Create() => new Fake(this);
        private sealed class Fake(Transfer data) : IVideoH3Preparation
        {
            public IReadOnlyList<ManagedModelArtifactCard> Cards { get; } = [Card()];
            public int ParallelConnections { get; set; }
            public IReadOnlyList<VideoH3FilePresence> Inspect() => [new("bundle", "a.bin", 100, true), new("bundle", "b.bin", data.Complete ? 200 : 40, data.Complete)];
            public async Task<bool> PrepareAsync(bool download, IProgress<ManagedModelDownloadProgress>? progress, CancellationToken token)
            {
                if (!download) { data.Checks++; return data.Complete; }
                data.Downloads++; progress?.Report(new("bundle", "b.bin", 180, 300, 3 * 1024 * 1024, "downloading"));
                await data.Finish.Task.WaitAsync(token); data.Complete = true;
                progress?.Report(new("bundle", "", 300, 300, 0, "verified")); return true;
            }
            public void Dispose() { }
        }
    }
    private static async Task Until(Func<bool> predicate)
    { for (var i = 0; i < 200 && !predicate(); i++) await Task.Delay(10); Assert.IsTrue(predicate()); }
    [TestMethod, DataRow("ru", true), DataRow("en", false)]
    public Task NextHostsPageWithSpeedAndLiveStatusesThenBackReturnsToSameProject(string language, bool dark) => Ui.Value.InvokeAsync(async () =>
    {
        using var f = new VideoPreparationTests.TestFiles(); var p = VideoPreparationFinishTests.ReadyProject(); p.CurrentStep = "finish";
        VideoGeneratorPlan.SaveChoice(p, new("minimax-h3", 1280, 720, 24, -1, ""));
        var path = f.File("p.lopataAIVid"); VideoPreparationArchive.Save(p, path); var session = new VideoPreparationSession(f.File("recovery")); session.Load(path);
        var l = new LocalizationService(); l.Load(language); var host = new VideoPreparationControl(l.T, session); var transfer = new Transfer();
        host.ConfigureGeneratorPreparation(_ => host.ShowH3Preparation(f.File("models"), 4, transfer.Create));
        var owner = new Window { Content = host, Width = 1250, Height = 850 };
        owner.Resources["TextPrimaryBrush"] = new SolidColorBrush(dark ? Colors.White : Colors.Black);
        owner.Resources["PanelBrush"] = new SolidColorBrush(dark ? Color.FromRgb(24, 33, 49) : Colors.WhiteSmoke);
        owner.Resources["WindowBackgroundBrush"] = new SolidColorBrush(dark ? Color.FromRgb(16, 24, 39) : Colors.White);
        owner.Resources["LineBrush"] = new SolidColorBrush(Colors.Gray); owner.Resources["SecondaryButtonBackgroundBrush"] = owner.Resources["PanelBrush"];
        owner.Resources["AccentBrush"] = new SolidColorBrush(Colors.RoyalBlue); owner.Resources["UiBodyFontSize"] = 14d; owner.Resources["UiSectionFontSize"] = 18d;
        try
        {
            owner.Show(); Assert.IsTrue(host.Next()); var page = (VideoH3PreparationControl)host.Content;
            await Until(() => transfer.Checks == 1 && !page.Busy); Assert.AreEqual(0, transfer.Downloads); Assert.AreEqual(0, owner.OwnedWindows.Count);
            Assert.AreEqual(Visibility.Collapsed, Find<Button>(page, "VideoH3.Next").Visibility);
            Find<Button>(page, "VideoH3.Next").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.AreEqual(0, ScenarioNavigationTests.LogicalDescendants(page).OfType<VideoH3WorkspaceControl>().Count());
            Assert.AreEqual("finish", session.Project.CurrentStep); Assert.IsFalse(host.Next());
            StringAssert.Contains(Find<TextBlock>(page, "VideoH3.File.bundle.a.bin").Text, l.T("VideoPrep.H3File.downloaded"));
            Find<Button>(page, "VideoH3.Download").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Find<TextBlock>(page, "VideoH3.Speed").Text.Contains("3", StringComparison.Ordinal));
            StringAssert.Contains(Find<TextBlock>(page, "VideoH3.File.bundle.b.bin").Text, l.T("VideoPrep.H3File.downloading"));
            Assert.AreEqual(Visibility.Collapsed, Find<Button>(page, "VideoH3.Next").Visibility);
            Save(page, language + "-downloading");
            Find<Button>(page, "VideoH3.Cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => !page.Busy);
            StringAssert.Contains(Find<TextBlock>(page, "VideoH3.File.bundle.b.bin").Text, l.T("VideoPrep.H3File.partial"));
            Assert.AreEqual(Visibility.Collapsed, Find<Button>(page, "VideoH3.Next").Visibility);
            transfer.Finish.TrySetResult(); await page.RunAsync(true); Assert.IsTrue(page.Ready);
            Assert.AreSame(page, host.Content); Assert.IsFalse(Find<Button>(page, "VideoH3.Download").IsEnabled);
            Assert.AreEqual(l.T("VideoPrep.H3FilesReady"), Find<TextBlock>(page, "VideoH3.Status").Text); Save(page, language + "-ready");
            Assert.AreEqual(Visibility.Visible, Find<Button>(page, "VideoH3.Next").Visibility);
            Find<Button>(page, "VideoH3.Next").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.AreEqual(1, ScenarioNavigationTests.LogicalDescendants(page).OfType<VideoH3WorkspaceControl>().Count());
            Assert.AreEqual(0, owner.OwnedWindows.Count); Save(page, language + "-workspace");
            Find<Button>(page, "VideoH3.Workspace.Back").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.AreEqual(Visibility.Visible, Find<Button>(page, "VideoH3.Next").Visibility);
            Assert.IsTrue(host.BackStepIfNeeded()); Assert.AreNotSame(page, host.Content); Assert.AreEqual("finish", session.Project.CurrentStep);
            Assert.AreEqual("minimax-h3", VideoGeneratorPlan.Selected(session.Project)!.Settings.ModelId);
        }
        finally { owner.Close(); }
    }).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(35));
    [TestMethod]
    public Task WorkspaceHomeUsesNavigationHomeAndOpeningAnotherProjectLeavesOldModelPage() => Ui.Value.InvokeAsync(async () =>
    {
        using var f = new VideoPreparationTests.TestFiles(); var l = new LocalizationService(); l.Load("ru");
        var p = VideoPreparationFinishTests.ReadyProject(); p.CurrentStep = "finish";
        VideoGeneratorPlan.SaveChoice(p, new("minimax-h3", 1280, 720, 24, -1, ""));
        var path = f.File("p.lopataAIVid"); VideoPreparationArchive.Save(p, path);
        var navigation = new ScenarioNavigationControl(); navigation.Configure(l.T);
        navigation.SelectDirection(ScenarioNavigationCatalog.Creation);
        var session = new VideoPreparationSession(f.File("recovery")); session.Load(path);
        var host = new VideoPreparationControl(l.T, session); host.HomeRequested += navigation.ShowHome;
        var transfer = new Transfer { Complete = true };
        host.ConfigureGeneratorPreparation(_ => host.ShowH3Preparation(f.File("models"), 4, transfer.Create));
        var owner = new Window { Content = host, Width = 1250, Height = 850 };
        try
        {
            owner.Show(); Assert.IsTrue(host.Next()); var page = (VideoH3PreparationControl)host.Content;
            await Until(() => !page.Busy && page.Ready);
            Find<Button>(page, "VideoH3.Next").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Find<Button>(page, "VideoH3.Workspace.Home").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.IsTrue(navigation.IsHome); Assert.IsFalse(navigation.IsVideoPreparation);
            Assert.AreEqual(p.Id, host.Session.Project.Id); Assert.AreEqual("finish", host.Session.Project.CurrentStep);
            Assert.AreEqual("minimax-h3", VideoGeneratorPlan.Selected(VideoPreparationArchive.Load(path, f.File("readback")))!.Settings.ModelId);
            owner.Content = null; owner.Content = host;
            Assert.AreSame(page, host.Content); Assert.AreEqual(1, ScenarioNavigationTests.LogicalDescendants(page).OfType<VideoH3WorkspaceControl>().Count());
            Assert.AreEqual(1, transfer.Checks); Assert.AreEqual(0, transfer.Downloads);
            var other = VideoPreparationFinishTests.ReadyProject(); other.CurrentStep = "form";
            var otherPath = f.File("other.lopataAIVid"); VideoPreparationArchive.Save(other, otherPath);
            Assert.IsTrue(host.OpenPath(otherPath)); Assert.AreNotSame(page, host.Content); Assert.AreEqual(other.Id, host.Session.Project.Id);
        }
        finally { owner.Close(); }
    }).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(35));
    [TestMethod]
    public Task BackWhileDownloadingCancelsBeforeLeavingAndUnloadingAlsoCancels() => Ui.Value.InvokeAsync(async () =>
    {
        var l = new LocalizationService(); l.Load("ru"); var transfer = new Transfer();
        var page = new VideoH3PreparationControl(l.T, "test root", 4, transfer.Create); var owner = new Window { Content = page }; var back = 0;
        page.BackRequested += () => back++;
        try
        {
            owner.Show(); await Until(() => transfer.Checks == 1 && !page.Busy);
            var running = page.RunAsync(true); await Until(() => transfer.Downloads == 1); page.RequestBack(); await running; Assert.AreEqual(1, back);
            running = page.RunAsync(true); await Until(() => transfer.Downloads == 2); owner.Content = null; await running;
            Assert.IsFalse(page.Busy); Assert.AreEqual(1, back);
        }
        finally { owner.Close(); }
    }).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(30));
    private static void Save(FrameworkElement page, string name)
    {
        var folder = Environment.GetEnvironmentVariable("LOPATA_H3_PAGE_IMAGES"); if (folder is null) return;
        page.UpdateLayout(); var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)page.ActualWidth, (int)page.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(page); var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        System.IO.Directory.CreateDirectory(folder); using var output = System.IO.File.Create(System.IO.Path.Combine(folder, name + ".png")); encoder.Save(output);
    }
    [TestMethod]
    public Task FullManifestScrollsAndFooterRemainsVisibleOnSmallWindow() => Ui.Value.InvokeAsync(async () =>
    {
        using var f = new VideoPreparationTests.TestFiles(); var l = new LocalizationService(); l.Load("ru");
        var host = new VideoPreparationControl(l.T); var root = f.File("models");
        var store = new ManagedModelLibraryStore(f.File("library"));
        var page = host.ShowH3Preparation(root, 4, () => new VideoH3Preparation(root, store)); var owner = new Window { Content = host, Width = 820, Height = 640 };
        owner.Resources["TextPrimaryBrush"] = Brushes.White; owner.Resources["WindowBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(16, 24, 39));
        owner.Resources["UiBodyFontSize"] = 14d; owner.Resources["UiSectionFontSize"] = 18d;
        // Local verification of an empty isolated folder must never download anything.
        using var audit = ComponentAudit.UseTestStore(f.File("audit"), false);
        try
        {
            owner.Show(); await Until(() => !page.Busy); page.UpdateLayout();
            var scroll = Find<ScrollViewer>(page, "VideoH3.Files"); Assert.IsGreaterThan(scroll.ViewportHeight, scroll.ExtentHeight);
            var button = Find<Button>(page, "VideoH3.Download"); Assert.IsTrue(button.TransformToAncestor(page).Transform(new Point()).Y < page.ActualHeight - 30);
            scroll.ScrollToEnd(); page.UpdateLayout(); Assert.IsGreaterThan(0d, scroll.VerticalOffset); Save(page, "ru-full-manifest");
        }
        finally { owner.Close(); }
    }).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(30));
}
