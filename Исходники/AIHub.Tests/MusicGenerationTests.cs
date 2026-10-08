using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicGenerationTests
{
    [TestMethod]
    public void TruncatedNativeAudioCannotBecomeAReadyTrack()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write(0x46464952u); writer.Write(192036u); writer.Write(0x45564157u);
                writer.Write(0x20746d66u); writer.Write(16u); writer.Write((ushort)1); writer.Write((ushort)2);
                writer.Write(48000u); writer.Write(192000u); writer.Write((ushort)4); writer.Write((ushort)16);
                writer.Write(0x61746164u); writer.Write(192000u); writer.Write(new byte[192000]);
            }
            Assert.AreEqual(TimeSpan.FromSeconds(1), MusicWaveFile.ReadDuration(path));
            using (var stream = File.OpenWrite(path)) stream.SetLength(new FileInfo(path).Length - 4);
            Assert.ThrowsExactly<InvalidDataException>(() => MusicWaveFile.ReadDuration(path));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void ContextReservesActualOutputAndFixedSeedsSurviveNativeSerialization()
    {
        var request = new MusicYueRequest("rock", "Тест", 123, 456);
        request.CheckContext(new CharacterTokenizer(), default);
        Assert.ThrowsExactly<InvalidDataException>(() => (request with { Lyrics = new string('x', MusicTextBudget.FillLimit - 100) }).CheckContext(new CharacterTokenizer(), default));
        Assert.ThrowsExactly<InvalidDataException>(() => (request with { Abc = new string('x', MusicTextBudget.FillLimit) }).CheckContext(new CharacterTokenizer(), default));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => (request with { SoundSeed = -1 }).Validate());
        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize((request with { Abc = "X:1\nK:C\nC" }).NativeRequest));
        Assert.AreEqual(123, serialized.RootElement.GetProperty("lm_seed").GetInt32());
        Assert.AreEqual(456, serialized.RootElement.GetProperty("seed").GetInt32());
        Assert.AreEqual("wav16", serialized.RootElement.GetProperty("output_format").GetString());
        Assert.IsTrue(serialized.RootElement.GetProperty("abc").GetString()!.StartsWith("X:1"));
        using var shortRequest = JsonDocument.Parse(JsonSerializer.Serialize((request with { DurationSeconds = 1 }).NativeRequest));
        var sampling = shortRequest.RootElement.GetProperty("semantic_sampling");
        Assert.AreEqual(25, sampling.GetProperty("max_tokens").GetInt32());
        Assert.IsTrue(sampling.GetProperty("min_tokens").GetInt32() <= sampling.GetProperty("max_tokens").GetInt32());
    }

    [TestMethod]
    public async Task RefusedRuntimeConsentPreventsRequestsAndWorkerLaunch()
    {
        var previous = ComponentLicenseGate.ConfirmAsync; var calls = 0;
        ComponentLicenseGate.ConfirmAsync = (ids, _) =>
        { calls++; Assert.Contains(MusicYueRuntime.ComponentId, ids); throw new OperationCanceledException(); };
        var request = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => new MusicYueWorker("missing-runtime").PlanAsync("missing-model",
                new("rock", "Тест", 1, 2), request, request + ".abc", default));
            Assert.AreEqual(1, calls); Assert.IsFalse(File.Exists(request));
        }
        finally { ComponentLicenseGate.ConfirmAsync = previous; }
    }

    [TestMethod]
    [DataRow("ru", 280d, true)]
    [DataRow("en", 550d, false)]
    public Task CompactControlsExposeSafeStatesAndDoNotClip(string language, double width, bool dark) => ScenarioNavigationTests.Sta(() =>
    {
        using var files = new MusicProjectTests.Files();
        var control = new MusicGenerationControl(new MusicOutputPreferences(Path.Combine(files.Root, "output.json")));
        var l = new LocalizationService(); l.Load(language); control.Localize(l.T);
        var window = new Window { Content = control, Width = width, Height = 560, ShowInTaskbar = false, Left = -10000, Top = -10000,
            WindowStartupLocation = WindowStartupLocation.Manual, Background = dark ? Brushes.DarkSlateGray : Brushes.WhiteSmoke };
        window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/SettingsResources.xaml", UriKind.Relative) });
        window.Resources["TextPrimaryBrush"] = dark ? Brushes.White : Brushes.Black;
        Exception? failure = null;
        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            try
            {
                Assert.AreEqual(new MusicGenerationOptions("", 1, null), control.Options);
                var buttons = ScenarioNavigationTests.LogicalDescendants(control).OfType<Button>().ToArray();
                var start = buttons.Single(b => AutomationProperties.GetAutomationId(b) == "Music.Generation.StartPause");
                var cancel = buttons.Single(b => AutomationProperties.GetAutomationId(b) == "Music.Audio.CancelGeneration");
                var poetry = buttons.Single(b => AutomationProperties.GetAutomationId(b) == "Music.Audio.PoetryChat");
                control.UpdateState(true, false, false, false); Assert.IsFalse(start.IsEnabled); Assert.IsFalse(cancel.IsEnabled);
                control.UpdateState(true, false, false, true); Assert.IsTrue(start.IsEnabled); Assert.AreEqual(l.T("Music.Generation.Start"), start.ToolTip);
                control.SetHardware(l.T("Music.Hardware.Cpu") + " — " + l.T("Music.Hardware.GpuMemory"));
                var hardware = ScenarioNavigationTests.LogicalDescendants(control).OfType<TextBlock>()
                    .Single(t => AutomationProperties.GetAutomationId(t) == "Music.Generation.Hardware");
                StringAssert.Contains(hardware.Text, l.T("Music.Hardware.GpuMemory"));
                control.UpdateState(false, true, false, true); Assert.AreEqual(l.T("Music.Generation.Pause"), start.ToolTip); Assert.IsTrue(cancel.IsEnabled);
                control.UpdateState(false, false, true, true); Assert.AreEqual(l.T("Music.Generation.Resume"), start.ToolTip);
                control.UpdateState(true, false, false, false); Assert.IsFalse(poetry.IsEnabled); Assert.AreEqual(l.T("Music.Generation.Poetry"), poetry.ToolTip);
                window.UpdateLayout();
                Assert.AreEqual(56d, start.ActualWidth); Assert.AreEqual(56d, start.ActualHeight);
                foreach (var box in ScenarioNavigationTests.LogicalDescendants(control).OfType<ComboBox>())
                { var p = box.TranslatePoint(new(0, 0), control); Assert.IsTrue(p.X + box.ActualWidth <= control.ActualWidth + 1); }
                var directory = Environment.GetEnvironmentVariable("LOPATA_MUSIC_UI_EVIDENCE");
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory); var visual = new DrawingVisual();
                    using (var draw = visual.RenderOpen()) { var bounds = new Rect(control.RenderSize); draw.DrawRectangle(window.Background, null, bounds);
                        draw.DrawRectangle(new VisualBrush(control) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = bounds }, null, bounds); }
                    var bitmap = new RenderTargetBitmap((int)control.ActualWidth, (int)control.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
                    var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output = File.Create(Path.Combine(directory, "generation-" + language + ".png")); png.Save(output);
                }
            }
            catch (Exception error) { failure = error; }
            finally { window.Close(); }
        }));
        window.ShowDialog(); if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    });
    private sealed class CharacterTokenizer : IMusicTokenizer { public int Count(string text, CancellationToken cancellation = default) => text.Length; }
}
