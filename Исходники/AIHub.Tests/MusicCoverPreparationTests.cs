using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicCoverPreparationTests
{
    [TestMethod]
    [DataRow("ru")]
    [DataRow("en")]
    public Task MissingComponentsWaitForDownloadAndCloseOnlyAfterSuccessfulPreparation(string language) => ScenarioNavigationTests.Sta(() =>
    {
        var fake = new Preparation(); var control = Control(fake, language);
        var localizer = new LocalizationService(); localizer.Load(language);
        var window = new MusicCoverPreparationWindow(control, localizer.T, null);
        try
        {
            window.Show(); Pump(() => Find(window, "Generation.Download") is not null);
            Assert.IsTrue(window.IsVisible); Assert.IsFalse(window.Completion.IsCompleted);
            Assert.IsNull(Find(window, "Generation.OpenChat"));
            Find(window, "Generation.Download")!.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Pump(() => fake.PrepareCalls == 1);
            Assert.IsTrue(fake.DownloadRequested); Assert.IsTrue(window.IsVisible);
            Assert.AreEqual(8, fake.MaximumParallelConnections);
            fake.Complete.SetResult(); Pump(() => window.Completion.IsCompleted);
            Assert.IsTrue(window.Completion.Result); Assert.IsFalse(window.IsVisible);
            Assert.IsTrue(fake.Disposed);
        }
        finally { window.Close(); }
    });

    [TestMethod]
    public Task InstalledFilesStillWaitForLicenseRuntimePreparationBeforeAutoClose() => ScenarioNavigationTests.Sta(() =>
    {
        var fake = new Preparation { Installed = true }; var control = Control(fake, "ru");
        var window = new MusicCoverPreparationWindow(control, key => key, null);
        try
        {
            window.Show(); Pump(() => fake.PrepareCalls == 1);
            Assert.IsFalse(fake.DownloadRequested); Assert.IsTrue(window.IsVisible);
            fake.Complete.SetResult(); Pump(() => window.Completion.IsCompleted);
            Assert.IsTrue(window.Completion.Result);
        }
        finally { window.Close(); }
    });

    [TestMethod]
    public Task FailureRemainsVisibleAndClosingCancelsPreparation() => ScenarioNavigationTests.Sta(() =>
    {
        var fake = new Preparation(); var control = Control(fake, "ru");
        var window = new MusicCoverPreparationWindow(control, key => key, null);
        window.Show(); Pump(() => Find(window, "Generation.Download") is not null);
        Find(window, "Generation.Download")!.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Pump(() => fake.PrepareCalls == 1); fake.Complete.SetException(new IOException("Controlled failure"));
        Pump(() => Find(window, "Generation.Download")?.IsEnabled == true);
        Assert.IsTrue(window.IsVisible); Assert.IsFalse(window.Completion.IsCompleted);
        fake.Complete = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Find(window, "Generation.Download")!.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Pump(() => fake.PrepareCalls == 2); window.Close();
        Pump(() => fake.Canceled);
        Assert.IsFalse(window.Completion.Result);
    });

    private static ImageGenerationControl Control(Preparation fake, string language)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var storage = new StorageSettings();
        storage.Models.Locations.Add(new() { Path = Path.GetTempPath() });
        var localizer = new LocalizationService(); localizer.Load(language);
        var control = new ImageGenerationControl(fake, new()); control.Configure(localizer.T, storage, 8); return control;
    }
    private static System.Windows.Controls.Button? Find(DependencyObject root, string id) =>
        ScenarioNavigationTests.LogicalDescendants(root).OfType<System.Windows.Controls.Button>()
            .FirstOrDefault(b => AutomationProperties.GetAutomationId(b) == id);
    private static void Pump(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5); var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
        timer.Tick += (_, _) => { if (done() || DateTime.UtcNow > deadline) frame.Continue = false; };
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        Assert.IsTrue(done(), "UI operation did not complete.");
    }
    private sealed class Preparation : IImageGenerationPreparation
    {
        public int MaximumParallelConnections { get; set; }
        public bool Installed, Disposed, DownloadRequested, Canceled;
        public int PrepareCalls;
        public TaskCompletionSource Complete = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IReadOnlyList<ManagedModelArtifactCard> Cards() => [new() { ModelArtifactId="generation-z-image", DisplayName="Z-Image Turbo", Status=Installed ? ManagedModelStatuses.Installed : ManagedModelStatuses.NotInstalled }];
        public bool IsReady(string root, string modelId) => Installed;
        public IReadOnlyList<ManagedModelArtifactCard> Register(string root, string modelId) => Cards();
        public Task<IReadOnlyList<ManagedModelArtifactCard>> CheckAsync(string root, string modelId, IProgress<ManagedModelDownloadProgress>? progress, CancellationToken token) => Task.FromResult(Cards());
        public async Task<IReadOnlyList<ManagedModelArtifactCard>> PrepareAsync(string root, string modelId, bool download, IProgress<ManagedModelDownloadProgress>? progress, CancellationToken token)
        {
            PrepareCalls++; DownloadRequested=download;
            try { await Complete.Task.WaitAsync(token); Installed=true; return Cards(); }
            catch (OperationCanceledException) { Canceled=true; throw; }
        }
        public void Dispose() => Disposed=true;
    }
}
