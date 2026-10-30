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
using PasswordBox = System.Windows.Controls.PasswordBox;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoLtxPreparationUiTests
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
    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject => ScenarioNavigationTests.LogicalDescendants(root)
        .OfType<T>().Single(e => AutomationProperties.GetAutomationId(e) == id);
    private static async Task Until(Func<bool> condition)
    { for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10); Assert.IsTrue(condition()); }
    private sealed class Transfer
    {
        internal int Checks, Downloads;
        internal bool Complete, RejectAccess;
        internal string LastToken = "";
        internal TaskCompletionSource Finish = new();
        internal IVideoLtxPreparation Create() => new Fake(this);
        private sealed class Fake(Transfer transfer) : IVideoLtxPreparation
        {
            public IReadOnlyList<ManagedModelArtifactCard> Cards { get; } = [new()
            {
                ModelArtifactId = "ltx-fixture", DisplayName = "LTX fixture", Revision = new string('a', 40),
                Files = [new() { RelativePath = "a.bin", SizeBytes = 100 }, new() { RelativePath = "b.bin", SizeBytes = 200 }]
            }];
            public int ParallelConnections { get; set; }
            public string AccessToken { set => transfer.LastToken = value; }
            public IReadOnlyList<VideoH3FilePresence> Inspect() =>
                [new("ltx-fixture", "a.bin", 100, true), new("ltx-fixture", "b.bin", transfer.Complete ? 200 : 40, transfer.Complete)];
            public async Task<bool> PrepareAsync(bool download, IProgress<ManagedModelDownloadProgress>? progress, CancellationToken token)
            {
                if (!download) { transfer.Checks++; return transfer.Complete; }
                transfer.Downloads++;
                if (transfer.RejectAccess) throw new InvalidOperationException("VideoPrep.LtxAccessRequired");
                progress?.Report(new("ltx-fixture", "b.bin", 180, 300, 3 * 1024 * 1024, "downloading"));
                await transfer.Finish.Task.WaitAsync(token); transfer.Complete = true;
                progress?.Report(new("ltx-fixture", "", 300, 300, 0, "verified")); return true;
            }
            public void Dispose() { }
        }
    }
    private static Window Owner(FrameworkElement content, bool dark, double width = 1120, double height = 760)
    {
        var owner = new Window { Content = content, Width = width, Height = height, UseLayoutRounding = true,
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI") };
        // Use the scenario's real templates, inherited from VideoPreparationControl in the app.
        owner.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/AIHub;component/Controls/VideoPreparationResources.xaml", UriKind.Relative) });
        // The palette matches MainWindow.ApplyTheme; constructing MainWindow would start unrelated services.
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
    [TestMethod, DataRow("ru", true), DataRow("en", false)]
    public Task InlinePageWaitsForDownloadShowsProgressAndOnlyOpensPendingStageWhenReady(string language, bool dark) => Ui.Value.InvokeAsync(async () =>
    {
        var localization = new LocalizationService(); localization.Load(language);
        var transfer = new Transfer(); var page = new VideoLtxPreparationControl(localization.T, "fixture", 4, transfer.Create);
        var owner = Owner(page, dark); var back = 0; var home = 0;
        page.BackRequested += () => back++; page.HomeRequested += () => home++;
        try
        {
            owner.Show(); await Until(() => transfer.Checks == 1 && !page.Busy);
            Assert.AreEqual(0, transfer.Downloads); Assert.AreEqual(0, owner.OwnedWindows.Count);
            Assert.IsNotNull(Find<Button>(page, "VideoLtx.OpenModel"));
            Assert.IsNotNull(Find<Button>(page, "VideoLtx.OpenTokenSettings"));
            Assert.AreEqual(Visibility.Collapsed, Find<Button>(page, "VideoLtx.Next").Visibility);
            StringAssert.Contains(Find<TextBlock>(page, "VideoLtx.File.ltx-fixture.b.bin").Text, localization.T("VideoPrep.H3File.partial"));
            var token = "hf_ui_fixture_only";
            Find<PasswordBox>(page, "VideoLtx.AccessToken").Password = token;
            page.Configure(localization.T);
            Assert.AreEqual(token, Find<PasswordBox>(page, "VideoLtx.AccessToken").Password);
            Assert.IsFalse(ScenarioNavigationTests.LogicalDescendants(page).OfType<TextBlock>().Any(t => t.Text.Contains(token, StringComparison.Ordinal)));
            Find<Button>(page, "VideoLtx.Download").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => transfer.Downloads == 1 && Find<TextBlock>(page, "VideoLtx.Speed").Text.Contains("3", StringComparison.Ordinal));
            Assert.AreEqual(token, transfer.LastToken);
            StringAssert.Contains(Find<TextBlock>(page, "VideoLtx.File.ltx-fixture.b.bin").Text, localization.T("VideoPrep.H3File.downloading"));
            Assert.IsFalse(Find<Button>(page, "VideoLtx.Download").IsEnabled);
            Save(page, language + "-downloading");
            Find<Button>(page, "VideoLtx.Cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => !page.Busy); Assert.IsFalse(page.Ready);
            transfer.Finish.TrySetResult(); await page.RunAsync(true);
            Assert.IsTrue(page.Ready); Assert.AreEqual(Visibility.Visible, Find<Button>(page, "VideoLtx.Next").Visibility);
            Assert.AreEqual(localization.T("VideoPrep.LtxFilesReady"), Find<TextBlock>(page, "VideoLtx.Status").Text);
            Save(page, language + "-ready");
            Find<Button>(page, "VideoLtx.Next").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.AreEqual(localization.T("VideoPrep.LtxNextHint"), Find<TextBlock>(page, "VideoLtx.Next.Hint").Text);
            Assert.IsFalse(ScenarioNavigationTests.LogicalDescendants(page).OfType<Button>()
                .Any(b => AutomationProperties.GetAutomationId(b).Contains("Generate", StringComparison.Ordinal)));
            Assert.AreEqual(0, owner.OwnedWindows.Count);
            Save(page, language + "-next");
            Find<Button>(page, "VideoLtx.Next.Back").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.AreEqual("", Find<PasswordBox>(page, "VideoLtx.AccessToken").Password);
            Assert.AreEqual(Visibility.Visible, Find<Button>(page, "VideoLtx.Next").Visibility);
            Assert.IsTrue(page.CanCheckpoint());
            Find<Button>(page, "VideoLtx.Home").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.AreEqual(1, home);
            page.RequestBack(); Assert.AreEqual(1, back);
        }
        finally { owner.Close(); }
    }).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(30));

    [TestMethod]
    public Task BackWhileDownloadingCancelsBeforeNavigationAndUnloadClearsToken() => Ui.Value.InvokeAsync(async () =>
    {
        var localization = new LocalizationService(); localization.Load("ru"); var transfer = new Transfer();
        var page = new VideoLtxPreparationControl(localization.T, "fixture", 4, transfer.Create);
        var owner = Owner(page, true); var back = 0; page.BackRequested += () => { Assert.IsFalse(page.Busy); back++; };
        try
        {
            owner.Show(); await Until(() => transfer.Checks == 1 && !page.Busy);
            Find<PasswordBox>(page, "VideoLtx.AccessToken").Password = "hf_transient";
            var task = page.RunAsync(true); await Until(() => transfer.Downloads == 1);
            Assert.IsTrue(page.RequestExit()); await task;
            Assert.AreEqual(1, back); Assert.IsFalse(page.Busy);
            Assert.AreEqual("", Find<PasswordBox>(page, "VideoLtx.AccessToken").Password);
            Find<PasswordBox>(page, "VideoLtx.AccessToken").Password = "hf_second";
            task = page.RunAsync(true); await Until(() => transfer.Downloads == 2);
            owner.Content = null; await task;
            Assert.IsFalse(page.Busy); Assert.AreEqual(1, back);
            Assert.AreEqual("", Find<PasswordBox>(page, "VideoLtx.AccessToken").Password);
        }
        finally { owner.Close(); }
    }).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(30));

    [TestMethod]
    public Task AccessFailureIsExplainedWithoutEchoingCredential() => Ui.Value.InvokeAsync(async () =>
    {
        var localization = new LocalizationService(); localization.Load("en");
        var transfer = new Transfer { RejectAccess = true };
        var page = new VideoLtxPreparationControl(localization.T, "fixture", 4, transfer.Create); var owner = Owner(page, false);
        try
        {
            owner.Show(); await Until(() => !page.Busy && transfer.Checks == 1);
            var token = "hf_rejected_fixture"; Find<PasswordBox>(page, "VideoLtx.AccessToken").Password = token;
            await page.RunAsync(true);
            Assert.AreEqual(localization.T("VideoPrep.LtxAccessRequired"), Find<TextBlock>(page, "VideoLtx.Status").Text);
            Assert.IsFalse(page.Ready); Assert.IsTrue(Find<Button>(page, "VideoLtx.Download").IsEnabled);
            Assert.IsFalse(ScenarioNavigationTests.LogicalDescendants(page).OfType<TextBlock>().Any(t => t.Text.Contains(token, StringComparison.Ordinal)));
        }
        finally { owner.Close(); }
    }).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(30));

    [TestMethod]
    public Task ArchivedLtxChoiceCannotRouteButOpeningAndCancellingRetainsItsSettingsAndHistory() => Ui.Value.InvokeAsync(async () =>
    {
        using var files = new VideoPreparationTests.TestFiles(); var localization = new LocalizationService(); localization.Load("ru");
        var project = VideoPreparationFinishTests.ReadyProject(); project.CurrentStep = "finish";
        var selected = VideoGeneratorPlan.SaveChoice(project, new("ltx-2.5", 1280, 720, 24, -1, "saved settings"));
        var path = files.File("p.lopataAIVid"); VideoPreparationArchive.Save(project, path);
        var session = new VideoPreparationSession(files.File("recovery")); session.Load(path);
        var bytes = System.IO.File.ReadAllBytes(path); var host = new VideoPreparationControl(localization.T, session);
        var routes = 0; host.ConfigureGeneratorPreparation(_ => routes++);
        var owner = Owner(host, true);
        try
        {
            owner.Show(); Assert.AreEqual(project.Id, session.Project.Id);
            Assert.IsFalse(VideoGeneratorPlan.CanResume(session.Project));
            Assert.IsFalse(VideoGeneratorPlan.IsAvailable("ltx-2.5"));
            _ = owner.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                var picker = owner.OwnedWindows.OfType<VideoGeneratorChoiceWindow>().Single();
                var list = Find<System.Windows.Controls.ListBox>(picker, "VideoPrep.ModelPicker.List");
                Assert.IsFalse(list.Items.Cast<VideoGeneratorCandidate>().Any(c => c.Id == "ltx-2.5"));
                Find<Button>(picker, "VideoPrep.ModelPicker.Cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));
            Assert.IsFalse(host.Next()); Assert.AreEqual(0, routes);
            Assert.IsFalse(host.Content is VideoLtxPreparationControl);
            Assert.AreEqual("finish", session.Project.CurrentStep);
            Assert.AreEqual(selected.Id, VideoGeneratorPlan.Selected(session.Project)!.Id);
            Assert.AreEqual("saved settings", VideoGeneratorPlan.Selected(session.Project)!.Settings.Notes);
            Assert.AreEqual(1, session.Project.Generator!.History.Count);
            Assert.IsTrue(VideoGeneratorPlan.Candidates.Any(c => c.Id == "ltx-2.5"));
            CollectionAssert.AreEqual(bytes, System.IO.File.ReadAllBytes(path));
            await Task.CompletedTask;
        }
        finally { owner.Close(); }
    }).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(30));

    private sealed class CatalogOnly(string root) : IVideoLtxPreparation
    {
        public IReadOnlyList<ManagedModelArtifactCard> Cards { get; } = VideoLtxCatalog.Cards(root);
        public int ParallelConnections { get; set; }
        public string AccessToken { set { } }
        public IReadOnlyList<VideoH3FilePresence> Inspect() => Cards.SelectMany(card => card.Files.Select(file =>
            new VideoH3FilePresence(card.ModelArtifactId, file.RelativePath, 0, false))).ToArray();
        public Task<bool> PrepareAsync(bool download, IProgress<ManagedModelDownloadProgress>? progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.IsFalse(download, "This real-catalog presentation test must never download weights.");
            return Task.FromResult(false);
        }
        public void Dispose() { }
    }

    [TestMethod, DataRow("ru", true), DataRow("en", false)]
    public Task FullCatalogUsesRealThemeAndScrollsWithoutHidingActionsAt900By650(string language, bool dark) => Ui.Value.InvokeAsync(async () =>
    {
        using var files = new VideoPreparationTests.TestFiles(); var localization = new LocalizationService(); localization.Load(language);
        var modelRoot = files.File("models");
        var page = new VideoLtxPreparationControl(localization.T, modelRoot, 4, () => new CatalogOnly(modelRoot));
        var owner = Owner(page, dark, 900, 650);
        try
        {
            owner.Show(); await Until(() => !page.Busy); page.UpdateLayout();
            var scroll = Find<ScrollViewer>(page, "VideoLtx.Files");
            Assert.IsTrue(scroll.ViewportHeight > 0); Assert.IsTrue(scroll.ExtentHeight > scroll.ViewportHeight);
            Assert.AreEqual(5, ScenarioNavigationTests.LogicalDescendants(page).OfType<TextBlock>()
                .Count(t => AutomationProperties.GetAutomationId(t).StartsWith("VideoLtx.File.", StringComparison.Ordinal)));
            var download = Find<Button>(page, "VideoLtx.Download");
            Assert.AreSame(owner.FindResource(typeof(Button)), download.Style);
            Assert.AreEqual(((SolidColorBrush)owner.Resources["SecondaryButtonBackgroundBrush"]).Color,
                ((SolidColorBrush)download.Background).Color);
            foreach (var id in new[] { "VideoLtx.Back", "VideoLtx.Home", "VideoLtx.Verify", "VideoLtx.Download", "VideoLtx.Cancel",
                "VideoLtx.OpenModel", "VideoLtx.OpenTokenSettings" })
            {
                var button = Find<Button>(page, id); var position = button.TransformToAncestor(page).Transform(new Point());
                Assert.IsTrue(position.X >= 0 && position.X + button.ActualWidth <= page.ActualWidth + 1, id + " overflows horizontally");
                Assert.IsTrue(position.Y >= 0 && position.Y + button.ActualHeight <= page.ActualHeight + 1, id + " is outside the page");
            }
            var token = Find<PasswordBox>(page, "VideoLtx.AccessToken"); Assert.IsTrue(token.ActualWidth > 100);
            Save(page, language + "-catalog-900x650-top");
            scroll.ScrollToEnd(); page.UpdateLayout(); Assert.IsTrue(scroll.VerticalOffset > 0);
            Save(page, language + "-catalog-900x650-bottom");
        }
        finally { owner.Close(); }
    }).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(30));

    private static void Save(FrameworkElement page, string name)
    {
        var folder = Environment.GetEnvironmentVariable("LOPATA_LTX_PAGE_IMAGES"); if (folder is null) return;
        page.UpdateLayout(); var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)page.ActualWidth, (int)page.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(page); var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        System.IO.Directory.CreateDirectory(folder); using var output = System.IO.File.Create(System.IO.Path.Combine(folder, name + ".png")); encoder.Save(output);
    }
}
