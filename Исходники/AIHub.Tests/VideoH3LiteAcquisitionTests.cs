using System.Net.Http;
using System.Text.Json;
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
public sealed class VideoH3LiteAcquisitionTests
{
    [TestMethod]
    public void PinsContainOnlyLightInputsAndHaveIndependentInstallIdentitiesAndLicenseTerms()
    {
        using var f = new VideoPreparationTests.TestFiles();
        var cards = VideoH3LiteCatalog.Cards(f.File("models"));
        Assert.AreEqual(5, cards.Count); Assert.IsTrue(cards.Sum(c => c.TotalBytes) < 20_000_000_000);
        var heavy = VideoH3Catalog.Cards(f.File("models"));
        Assert.IsFalse(cards.Any(c => heavy.Any(h => h.ModelArtifactId == c.ModelArtifactId || h.InstallDirectory == c.InstallDirectory)));
        Assert.IsTrue(cards.All(c => c.IsPinned && c.IsManaged && !c.CanRemoveFiles && c.Revision.Length == 40));
        var files = cards.SelectMany(c => c.Files).ToArray();
        Assert.IsTrue(files.Any(x => x.RelativePath == "minimax_h3_ref2va_pruned_w4a8_mixed.safetensors"));
        Assert.IsTrue(files.Any(x => x.RelativePath == "qwen3vl_4b_int4_convrot.safetensors"));
        Assert.IsTrue(files.Any(x => x.RelativePath == "mmh3-4b-ClipProj-v3.1-mlp.safetensors"));
        Assert.IsFalse(files.Any(x => x.RelativePath.Contains("32b", StringComparison.OrdinalIgnoreCase)));
        var licenses = new ComponentLicenseService(Path.Combine(AppContext.BaseDirectory, "Licenses"), f.File("receipts.json"));
        foreach (var card in cards)
        {
            var license = licenses.Entries.Single(e => e.Id == card.ModelArtifactId);
            Assert.IsFalse(license.Basic); Assert.AreEqual("download", license.Delivery); Assert.IsTrue(license.Terms.Length == 64);
            foreach (var text in license.Texts) Assert.IsTrue(licenses.ReadText(text).Length > 40);
            foreach (var file in card.Files)
            {
                Assert.IsTrue(file.SourceUrl.Contains(card.Revision, StringComparison.Ordinal));
                Assert.IsTrue(file.Sha256.Length == 64 && file.Sha256.All(Uri.IsHexDigit));
                Assert.IsTrue(file.SourceUrl.StartsWith("https://", StringComparison.Ordinal));
            }
        }
        Assert.Throws<InvalidOperationException>(() => VideoH3LiteCatalog.Cards("relative"));
        Assert.Throws<InvalidOperationException>(() => VideoH3LiteCatalog.Cards(""));
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; throw new InvalidOperationException("An automatic network request is forbidden in this test."); }
    }
    [TestMethod, DataRow(false), DataRow(true)]
    public async Task OpeningMissingBundleChecksLocallyWithoutDownloadOrLicensePrompt(bool auditEnabled)
    {
        using var f = new VideoPreparationTests.TestFiles(); using var audit = ComponentAudit.UseTestStore(f.File("audit"), auditEnabled);
        using var network = new NoNetwork(); using var http = new HttpClient(network);
        var root = f.File("models");
        using var service = new VideoH3Preparation(root, new(f.File("library")), http, VideoH3LiteCatalog.Cards(root));
        Assert.IsFalse(await service.PrepareAsync(false, null, default)); Assert.AreEqual(0, network.Calls);
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "Video", "H3-W4A8")));
    }

    [TestMethod]
    public void ChoosingLightBundleRoundTripsWithoutChangingHeavyWorkspaceOrReenablingRejectedModels()
    {
        using var f = new VideoPreparationTests.TestFiles(); var p = VideoPreparationFinishTests.ReadyProject();
        var old = VideoGeneratorPlan.SaveChoice(p, new("minimax-h3", 480, 864, 24, 123, "original"));
        VideoH3WorkspaceState.Ensure(p);
        var before = JsonSerializer.Serialize(p.Generator!.H3);
        var light = VideoGeneratorPlan.SaveChoice(p, new(VideoH3LiteCatalog.ModelId, 480, 864, 24, 321, "experimental"));
        var path = f.File("roundtrip.lopataAIVid"); VideoPreparationArchive.Save(p, path);
        var loaded = VideoPreparationArchive.Load(path, f.File("opened"));
        Assert.AreEqual(VideoH3LiteCatalog.ModelId, VideoGeneratorPlan.Selected(loaded)!.Settings.ModelId);
        Assert.AreEqual(before, JsonSerializer.Serialize(loaded.Generator!.H3));
        CollectionAssert.AreEqual(new[] { old, light }, loaded.Generator.History.ToArray());
        Assert.AreEqual(old.Settings, VideoGeneratorPlan.SettingsForModel(loaded, "minimax-h3"));
        Assert.IsFalse(VideoGeneratorPlan.IsAvailable("ltx-2.5")); Assert.IsFalse(VideoGeneratorPlan.IsAvailable("wan-vace-1.3b"));
    }

    private static readonly Lazy<Dispatcher> Ui = new(() =>
    {
        var ready = new TaskCompletionSource<Dispatcher>(); var thread = new Thread(() =>
        { var d = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(d)); ready.SetResult(d); Dispatcher.Run(); }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return ready.Task.GetAwaiter().GetResult();
    });
    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject => ScenarioNavigationTests.LogicalDescendants(root)
        .OfType<T>().Single(e => AutomationProperties.GetAutomationId(e) == id);
    private sealed class Transfer(string root) : IVideoH3Preparation
    {
        public IReadOnlyList<ManagedModelArtifactCard> Cards { get; } = VideoH3LiteCatalog.Cards(root);
        public int ParallelConnections { get; set; }
        internal int Downloads, Checks;
        internal bool Complete;
        internal TaskCompletionSource Finish = new();
        public IReadOnlyList<VideoH3FilePresence> Inspect() => Cards.SelectMany(c => c.Files.Select(f =>
            new VideoH3FilePresence(c.ModelArtifactId, f.RelativePath, Complete ? f.SizeBytes : 0, Complete))).ToArray();
        public async Task<bool> PrepareAsync(bool download, IProgress<ManagedModelDownloadProgress>? progress, CancellationToken token)
        {
            if (!download) { Checks++; return Complete; }
            Downloads++; var c = Cards[0]; progress?.Report(new(c.ModelArtifactId, c.Files[0].RelativePath, 100, c.TotalBytes, 12345, "downloading"));
            await Finish.Task.WaitAsync(token); Complete = true; return true;
        }
        public void Dispose() { }
    }
    private static async Task Until(Func<bool> condition)
    { for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10); Assert.IsTrue(condition()); }

    [TestMethod, DataRow("ru", true), DataRow("en", false)]
    public Task LightPageIsEmbeddedShowsProgressAndNeverCreatesHeavyGenerator(string language, bool dark) => Ui.Value.InvokeAsync(async () =>
    {
        using var f = new VideoPreparationTests.TestFiles(); var l = new LocalizationService(); l.Load(language);
        var p = VideoPreparationFinishTests.ReadyProject(); p.CurrentStep = "finish";
        VideoGeneratorPlan.SaveChoice(p, new(VideoH3LiteCatalog.ModelId, 480, 864, 24, -1, ""));
        var path = f.File("p.lopataAIVid"); VideoPreparationArchive.Save(p, path);
        var session = new VideoPreparationSession(f.File("recovery")); session.Load(path);
        var host = new VideoPreparationControl(l.T, session); var transfer = new Transfer(f.File("models"));
        host.ConfigureGeneratorPreparation(s => { Assert.AreEqual(VideoH3LiteCatalog.ModelId, s.ModelId); host.ShowH3Preparation(f.File("models"), 4, () => transfer, lite: true); });
        var owner = new Window { Content = host, Width = 1100, Height = 800 };
        owner.Resources["TextPrimaryBrush"] = dark ? Brushes.White : Brushes.Black;
        owner.Resources["PanelBrush"] = dark ? new SolidColorBrush(Color.FromRgb(24, 33, 49)) : Brushes.WhiteSmoke;
        owner.Resources["WindowBackgroundBrush"] = dark ? new SolidColorBrush(Color.FromRgb(16, 24, 39)) : Brushes.White;
        owner.Resources["SecondaryButtonBackgroundBrush"] = owner.Resources["PanelBrush"];
        owner.Resources["LineBrush"] = Brushes.Gray; owner.Resources["AccentBrush"] = Brushes.RoyalBlue;
        owner.Resources["UiBodyFontSize"] = 14d; owner.Resources["UiSectionFontSize"] = 18d;
        try
        {
            owner.Show(); Assert.IsTrue(host.Next()); var page = (VideoH3PreparationControl)host.Content;
            await Until(() => transfer.Checks > 0 && !page.Busy); Assert.AreEqual(0, transfer.Downloads);
            Assert.AreEqual(0, owner.OwnedWindows.Count); Assert.IsFalse(host.Next());
            Assert.AreEqual(Visibility.Collapsed, Find<Button>(page, "VideoH3.Next").Visibility);
            Save(page, language + "-missing");
            var running = page.RunAsync(true); await Until(() => transfer.Downloads == 1);
            Assert.IsFalse(Find<Button>(page, "VideoH3.Verify").IsEnabled);
            transfer.Finish.TrySetResult(); await running;
            Assert.AreEqual(l.T("VideoPrep.H3LiteFilesReady"), Find<TextBlock>(page, "VideoH3.Status").Text);
            Assert.AreEqual(Visibility.Visible, Find<Button>(page, "VideoH3.Next").Visibility);
            Save(page, language + "-ready");
            Find<Button>(page, "VideoH3.Next").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.IsTrue(ScenarioNavigationTests.LogicalDescendants(page).OfType<VideoH3WorkspaceControl>().Any());
            Assert.IsTrue(Find<Button>(page, "VideoH3.Workspace.Generate").IsEnabled);
            Assert.IsTrue(ScenarioNavigationTests.LogicalDescendants(page).OfType<TextBlock>().Any(t => t.Text == VideoH3LiteCatalog.Name));
            Assert.IsNull(session.Project.Generator!.H3);
            Save(page, language + "-workspace");
            Find<Button>(page, "VideoH3.Workspace.Back").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.IsTrue(page.Ready); page.RequestBack(); Assert.AreNotSame(page, host.Content);
            Assert.AreEqual(VideoH3LiteCatalog.ModelId, VideoGeneratorPlan.Selected(session.Project)!.Settings.ModelId);
        }
        finally { owner.Close(); }
    }).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(30));

    [TestMethod]
    public Task HomeDuringTransferWaitsForCancellation() => Ui.Value.InvokeAsync(async () =>
    {
        using var f = new VideoPreparationTests.TestFiles(); var l = new LocalizationService(); l.Load("ru");
        var transfer = new Transfer(f.File("models"));
        var page = new VideoH3PreparationControl(l.T, f.File("models"), 4, () => transfer, lite: true);
        var owner = new Window { Content = page }; var home = 0; var back = 0;
        page.HomeRequested += () => home++; page.BackRequested += () => back++;
        try
        {
            owner.Show(); await Until(() => transfer.Checks > 0 && !page.Busy);
            var running = page.RunAsync(true); await Until(() => transfer.Downloads == 1);
            Find<Button>(page, "VideoH3.Home").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await running; Assert.IsFalse(page.Busy); Assert.AreEqual(1, home); Assert.AreEqual(0, back);
        }
        finally { owner.Close(); }
    }).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(30));
    private static void Save(FrameworkElement page, string name)
    {
        var folder = Environment.GetEnvironmentVariable("LOPATA_H3LITE_IMAGES"); if (folder is null) return;
        page.UpdateLayout(); var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)page.ActualWidth, (int)page.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(page); var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(folder); using var output = File.Create(Path.Combine(folder, name + ".png")); encoder.Save(output);
    }
}
