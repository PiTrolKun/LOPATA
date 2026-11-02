using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Services;
using AIHub.Services.SongClip;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class SongClipMotionUiTests
{
    [TestMethod]
    public void OldAttemptKeepsItsGlitchframeIdentityAndNewMotionDefaults()
    {
        var old = JsonSerializer.Deserialize<SongClipAttempt>("""
            {"Id":"old","ProjectId":"project","Song":"song.wav","Reference":"","Name":"old","Lyrics":"","Timings":"","Settings":{},"ModelsRoot":"root","Connections":4}
            """)!;
        Assert.AreEqual("glitchframe", old.Model);
        Assert.AreEqual(243, old.Settings.Motion.RawFrames);
        Assert.AreEqual(39, old.Settings.Motion.ContextFrames);
        Assert.AreEqual(20, old.Settings.Motion.Steps);
    }

    [TestMethod]
    [DataRow("ru")]
    [DataRow("en")]
    public Task MotionSelectionShowsItsSettingsAndRetainsIndependentProjectValues(string language) => OnUi(() =>
    {
        using var files = new VideoPreparationTests.TestFiles();
        var store = new SongClipStore(files.File("projects"));
        var localization = new LocalizationService(); localization.Load(language);
        var control = new SongClipControl(localization.T, store);
        var window = new Window { Content = control, Width = 1700, Height = 950, ShowInTaskbar = false };
        try
        {
            window.Show(); window.UpdateLayout();
            var nodes = ScenarioNavigationTests.LogicalDescendants(control).ToArray();
            T Find<T>(string id) where T : DependencyObject => nodes.OfType<T>()
                .Single(n => AutomationProperties.GetAutomationId(n) == "SongClip." + id);
            var model = Find<Button>("Model");
            Assert.IsTrue(control.SelectVariant("motion"));
            var motionSize = Find<ComboBox>("MotionSize");
            var motionSteps = Find<ComboBox>("MotionSteps");
            Assert.IsTrue(motionSize.IsVisible);
            Assert.IsTrue(motionSteps.IsVisible);
            Assert.IsTrue(((TextBlock)model.Content).Text.Contains("Motion Context",StringComparison.Ordinal));
            motionSize.SelectedIndex = 2; motionSteps.SelectedItem = 24;
            Assert.IsTrue(control.Checkpoint());
            var saved = store.List().Single();
            Assert.AreEqual("h3_music_context", saved.Model);
            Assert.AreEqual(480, saved.Settings.Motion.Width);
            Assert.AreEqual(864, saved.Settings.Motion.Height);
            Assert.AreEqual(24, saved.Settings.Motion.Steps);
            Assert.AreEqual(20, saved.Settings.Steps, "H3 edits must not replace the Glitchframe step setting.");
            Assert.IsTrue(control.SelectVariant("glitchframe"));
            Assert.IsFalse(motionSize.IsVisible);
            Assert.IsFalse(motionSteps.IsVisible);
            control.OpenProject(saved.Id);
            Assert.IsTrue(((TextBlock)model.Content).Text.Contains("Glitchframe",StringComparison.Ordinal));
            Assert.IsTrue(control.SelectVariant("motion"));
            Assert.IsTrue(control.Checkpoint());
            control.OpenProject(saved.Id);
            Assert.IsTrue(((TextBlock)model.Content).Text.Contains("Motion Context",StringComparison.Ordinal));
            Assert.AreEqual(2, motionSize.SelectedIndex);
            Assert.AreEqual(24, motionSteps.SelectedItem);
            var copy = JsonSerializer.Deserialize<SongClipProject>(JsonSerializer.Serialize(saved))!;
            Assert.AreEqual(saved.Settings.Motion, copy.Settings.Motion);
        }
        finally { window.Close(); }
    });

    private static Task OnUi(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                action(); done.SetResult();
            }
            catch (Exception error) { done.SetException(error); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task;
    }
}
