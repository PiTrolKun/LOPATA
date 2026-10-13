using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Slider = System.Windows.Controls.Slider;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicPoetryCreativityTests
{
    [TestMethod]
    public void EveryCatalogModelUsesSelectedTemperatureForBothRequestKindsWithoutChangingOtherFields()
    {
        var context = new MusicPoetryContext("system", [new("user", "request", "turn")], 100, 32768, false);
        foreach (var candidate in MusicPoetryModelCatalog.All)
        {
            Assert.AreEqual("gguf", candidate.Format, "Current music candidates use the same temperature API.");
            var model = Model(candidate.Id);
            foreach (var assemble in new[] { false, true }) foreach (var stream in new[] { false, true })
            foreach (var value in new[] { .1, .65, 2 })
            {
                using var baseline = JsonDocument.Parse(MusicPoetryGeneration.Payload(model, context, 4096,
                    MusicPoetryReasoningOptions.Disabled, assemble, stream));
                using var selected = JsonDocument.Parse(MusicPoetryGeneration.Payload(model, context, 4096,
                    MusicPoetryReasoningOptions.Disabled, assemble, stream, value));
                Assert.AreEqual(value, selected.RootElement.GetProperty("temperature").GetDouble());
                Assert.AreEqual(MusicPoetryProfileCatalog.Find(candidate.Id)?.Temperature ?? .8,
                    baseline.RootElement.GetProperty("temperature").GetDouble());
                foreach (var field in baseline.RootElement.EnumerateObject().Where(p => p.Name != "temperature"))
                    Assert.AreEqual(field.Value.GetRawText(), selected.RootElement.GetProperty(field.Name).GetRawText(), candidate.Id + ": " + field.Name);
            }
        }
    }

    [TestMethod]
    public void ParsingRejectsInvalidNumbersAndAcceptsBothDecimalSeparators()
    {
        foreach (var input in new[] { "0.1", "0,1", " 2 ", "0,65", "1.9" }) Assert.IsTrue(MusicPoetryCreativity.TryParse(input, out _), input);
        foreach (var input in new[] { "", "0", "0.09", "2.1", "NaN", "Infinity", "1,000.0", "1e2", "abc" })
            Assert.IsFalse(MusicPoetryCreativity.TryParse(input, out _), input);
        foreach (var value in new[] { double.NaN, double.PositiveInfinity, -.1, 0, 2.1 })
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => MusicPoetryCreativity.Resolve(Model("core"), value));
    }

    [TestMethod]
    public void PreferencesRoundTripPerModelWithoutLosingLegacyOrFutureFields()
    {
        var root = Temporary();
        try
        {
            var path = Path.Combine(root, "preferences.json");
            File.WriteAllText(path, "{\"FavoriteId\":\"core\",\"Comments\":{\"core\":\"note\"},\"Reasoning\":{\"core\":{\"ModeId\":\"off\"}},\"FutureField\":{\"value\":7}}");
            var preferences = new MusicPoetryPreferences(path);
            Assert.AreEqual(0, preferences.Load().Creativity.Count);
            foreach (var id in MusicPoetryModelCatalog.All.Select(c => c.Id)) preferences.SetCreativity(id, id == "core" ? .1 : 2);
            var reopened = new MusicPoetryPreferences(path); Assert.AreEqual(.1, reopened.Load().Creativity["core"]);
            Assert.AreEqual(2, reopened.Load().Creativity["instruct"]);
            Assert.AreEqual("off", reopened.Load().Reasoning["core"].ModeId);
            Assert.AreEqual("note", reopened.Load().Comments["core"]); Assert.AreEqual("core", reopened.Load().FavoriteId);
            Assert.AreEqual(7, reopened.Load().Additional!["FutureField"].GetProperty("value").GetInt32());
            preferences.SetReasoning("core", "on"); preferences.SetComments(new Dictionary<string, string> { ["core"] = "updated" });
            Assert.AreEqual(.1, reopened.Load().Creativity["core"]);
            var before = File.ReadAllText(path);
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => preferences.SetCreativity("core", 3));
            Assert.ThrowsExactly<ArgumentException>(() => preferences.SetCreativity("unknown", 1));
            Assert.AreEqual(before, File.ReadAllText(path));
            File.WriteAllText(path, "{\"Creativity\":{\"core\":3}}");
            Assert.ThrowsExactly<InvalidDataException>(() => preferences.SetCreativity("core", 1));
            Assert.AreEqual("{\"Creativity\":{\"core\":3}}", File.ReadAllText(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public Task PopupSavesSwitchesAndKeepsThePreviousValueOnInvalidInputOrWriteFailure() => Sta(() =>
    {
        foreach (var language in new[] { "ru", "en" }) foreach (var dark in new[] { true, false })
        {
            var root = Temporary(); var path = Path.Combine(root, "preferences.json");
            var preferences = new MusicPoetryPreferences(path); preferences.SetFavorite("core");
            var runtime = new RepairRuntime(); var l = new LocalizationService(); l.Load(language);
            var editor = new MusicPoetryWindow(() => new() { Lyrics = "Keep lyrics" }, new(),
                new(new UserProfileStore(), new IpLocationService()), l.T, language, new(Path.Combine(root, "sessions")), runtime,
                preferences, () => [new(MusicPoetryModelCatalog.All.Single(c => c.Id == "core"), Model("core"))]);
            try
            {
                foreach (var key in new[] { "PanelBrush", "InputBrush", "WindowBackgroundBrush" }) editor.Resources[key] = dark ? Brushes.Black : Brushes.White;
                editor.Resources["TextPrimaryBrush"] = dark ? Brushes.White : Brushes.Black;
                editor.Resources["TextSecondaryBrush"] = editor.Resources["LineBrush"] = Brushes.Gray;
                editor.Resources["AccentBrush"] = editor.Resources["StepBadgeBrush"] = Brushes.RoyalBlue;
                editor.Resources["SecondaryButtonBackgroundBrush"] = Brushes.Gray; editor.Resources["UiBodyFontSize"] = 16d;
                editor.Show(); Select("core"); Open();
                var popup = Field<Popup>(editor, "_creativityPopup"); var slider = Field<Slider>(editor, "_creativitySlider");
                Screenshot(editor, language + "-" + dark + "-chat.png");
                Screenshot((FrameworkElement)popup.Child, language + "-" + dark + "-popup.png");
                Assert.AreEqual(.1, slider.Minimum); Assert.AreEqual(2, slider.Maximum); Assert.AreEqual(.1, slider.TickFrequency);
                Assert.AreEqual(.8, slider.Value); slider.Value = .1;
                Assert.AreEqual(.1, preferences.Load().Creativity["core"]);
                var input = Field<TextBox>(editor, "_creativityInput"); input.Text = "2"; Apply();
                Assert.AreEqual(2, preferences.Load().Creativity["core"]);
                input.Text = "NaN"; Apply(); Assert.AreEqual(2, Field<double>(editor, "_creativityValue"));
                Assert.AreEqual(Visibility.Visible, Field<TextBlock>(editor, "_creativityError").Visibility);
                input.Text = "0,65"; Apply(); Assert.AreEqual(.65, preferences.Load().Creativity["core"]);
                Select("instruct"); Assert.IsFalse(popup.IsOpen);
                Assert.AreEqual(.8, Field<double>(editor, "_creativityValue"));
                Assert.IsTrue(Field<Button>(editor, "_creativity").IsEnabled);
                Assert.IsFalse(Field<Button>(editor, "_reasoning").IsEnabled);
                Open(); Field<Slider>(editor, "_creativitySlider").Value = 1.9;
                Select("core"); Assert.AreEqual(.65, Field<double>(editor, "_creativityValue"));
                Open(); input = Field<TextBox>(editor, "_creativityInput");
                File.Delete(path); Directory.CreateDirectory(path); input.Text = "1"; Apply();
                Assert.AreEqual(.65, Field<double>(editor, "_creativityValue"));
                Assert.AreEqual(.65, Field<Slider>(editor, "_creativitySlider").Value);
                Directory.Delete(path); preferences.SetCreativity("core", .65);
                editor.Width = 900; editor.UpdateLayout(); Open();
                Screenshot(editor, language + "-" + dark + "-narrow.png");
                Field<TextBox>(editor, "_input").Text = "Improve";
                var task = (Task)typeof(MusicPoetryWindow).GetMethod("SendCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, [false])!;
                Pump(() => runtime.Values.Count == 1);
                Assert.IsFalse(Field<Popup>(editor, "_creativityPopup").IsOpen);
                Assert.IsFalse(Field<Button>(editor, "_creativity").IsEnabled);
                // Even an external settings change during a request cannot alter its repair sampler.
                preferences.SetCreativity("core", 2); runtime.Release.TrySetResult(); Pump(() => task.IsCompleted);
                task.GetAwaiter().GetResult(); CollectionAssert.AreEqual(new double?[] { .65, .65 }, runtime.Values.ToArray());
                Assert.AreEqual("Keep lyrics", Field<MusicPoetrySession>(editor, "_session").Lyrics);

                void Select(string id) { typeof(MusicPoetryWindow).GetField("_model", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(editor, Model(id)); Call(editor, "ModelCaption"); Call(editor, "Availability"); }
                void Open() => Field<Button>(editor, "_creativity").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                void Apply() => ScenarioNavigationTests.LogicalDescendants(Field<Popup>(editor, "_creativityPopup").Child).OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            finally { runtime.Release.TrySetResult(); editor.Close(); Pump(() => !editor.IsWorking); editor.Close(); Directory.Delete(root, true); }
        }
    });

    private static DebugModelInfo Model(string id)
    {
        var c = MusicPoetryModelCatalog.All.Single(c => c.Id == id);
        return new() { Name = c.FileName, Path = Path.Combine(Path.GetTempPath(), c.FileName), Format = c.Format, IsRunnable = true, IsCoreModel = id == "core" };
    }
    private static string Temporary() { var path = Path.Combine(Path.GetTempPath(), "lopata-creativity-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static T Field<T>(object o, string name) => (T)o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o)!;
    private static void Screenshot(FrameworkElement element, string name)
    {
        element.UpdateLayout(); var folder = Path.Combine(Path.GetTempPath(), "lopata-creativity-ui"); Directory.CreateDirectory(folder);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * 1.5), (int)Math.Ceiling(element.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
        bitmap.Render(element); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(folder, name)); encoder.Save(file);
    }
    private static void Call(object o, string name) => o.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(o, null);
    private static Task Sta(Action action) => ScenarioNavigationTests.Sta(() => { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext()); try { action(); } finally { SynchronizationContext.SetSynchronizationContext(null); } });
    private static void Pump(Func<bool> ready)
    {
        var start = DateTime.UtcNow; var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (ready() || DateTime.UtcNow - start > TimeSpan.FromSeconds(8)) { timer.Stop(); frame.Continue = false; } };
        timer.Start(); Dispatcher.PushFrame(frame); Assert.IsTrue(ready(), "UI operation timed out.");
    }
    private sealed class RepairRuntime : IMusicPoetryRuntime
    {
        public List<double?> Values { get; } = [];
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string> GenerateAsync(DebugModelInfo model, MusicPoetrySession request, string guidance,
            Action<MusicPoetryContext> contextReady, CancellationToken token, bool assemble = false, string? reasoningMode = null,
            Action<MusicPoetryStreamUpdate>? reasoningProgress = null, string? interfaceLanguage = null,
            MusicPoetryReasoningPreference? reasoningPreference = null, double? temperature = null)
        {
            Values.Add(temperature); await Release.Task.WaitAsync(token);
            return Values.Count == 1 ? "{\"chat\":\"promise\",\"song\":null,\"parameters\":null,\"variation\":null,\"actions\":[\"song\"]}"
                : "{\"chat\":\"discussion\",\"song\":null,\"parameters\":null,\"variation\":null,\"actions\":[]}";
        }
        public void Dispose() { }
    }
}
