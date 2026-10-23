using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VectorGraphics;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using CheckBox = System.Windows.Controls.CheckBox;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VectorSettingsSessionTests
{
    [TestMethod]
    public void AcceptedSettingsRemainBoundToRequestAndLegacyRequestsKeepTheirPrompt()
    {
        var options = new VectorGenerationOptions(16, 9, "icon", "outline", "detailed", "custom", "#123456 #ABCDEF", "color", "#334455");
        var request = new VectorRequest(Guid.NewGuid().ToString("N"), "Кот", "output", "author", DateTimeOffset.UtcNow, Options: options);
        var saved = JsonSerializer.Deserialize<VectorRequest>(JsonSerializer.Serialize(request))!;
        options = options with { Purpose = "logo", CanvasHeight = 16 };
        Assert.AreEqual("icon", saved.Options!.Purpose); Assert.AreEqual(9, saved.Options.CanvasHeight);
        var prompt = VectorPrompt.Compose(saved.Prompt, saved.Options);
        foreach (var part in new[] { "Кот", "16:9", "icon", "outline", "rich and meaningful detail", "#123456", "#ABCDEF", "#334455" }) StringAssert.Contains(prompt, part);
        Assert.AreEqual("Кот", saved.Prompt);
        var legacyJson = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(request))!.AsObject();
        legacyJson.Remove("SessionId"); legacyJson.Remove("Options");
        var legacy = legacyJson.Deserialize<VectorRequest>()!;
        Assert.IsNull(legacy.Options); Assert.AreEqual("Кот", VectorPrompt.Compose(legacy.Prompt, null));
        var invalid = new VectorGenerationOptions(Palette: "custom", Colors: "url(https://example.com)");
        Assert.Throws<InvalidDataException>(invalid.Validate);
        Assert.Throws<InvalidDataException>(() => new VectorGenerationOptions(CanvasHeight: 0).Validate());
    }

    [TestMethod]
    public void OnlyCurrentOrExplicitlyRestoredSessionIsVisibleAndClearPreservesFiles()
    {
        using var files = new Fixture();
        var session = new VectorSession(files.SessionRoot);
        var first = files.Add(session); var second = files.Add(session);
        var unrelated = new VectorSession(files.SessionRoot); files.Add(unrelated);
        Assert.HasCount(0, new VectorSession(files.SessionRoot).Visible());
        Assert.HasCount(2, session.Visible());
        var restored = new VectorSession(files.SessionRoot); restored.Restore(second.Request);
        CollectionAssert.AreEqual(new[] { first.Request.Id, second.Request.Id }, restored.Visible().Select(x => x.Request.Id).ToArray());
        restored.Clear(); Assert.HasCount(0, restored.Visible());
        Assert.IsTrue(File.Exists(VectorStore.FilePath(first.Request.Id, "image.svg")));
        Assert.AreEqual(first.Request, VectorStore.Load(first.Request.Id).Request);
        var third = files.Add(restored); Assert.AreEqual(third.Request.Id, restored.Visible().Single().Request.Id);
        var legacy = files.AddLegacy(); restored.Restore(legacy.Request);
        Assert.AreEqual(legacy.Request.Id, restored.Visible().Single().Request.Id);
    }

    [TestMethod]
    public Task DialogsSupportLocalizedKeyboardActionsAndWorkspaceClear() => ScenarioNavigationTests.Sta(() =>
    {
        using var files = new Fixture();
        var l = new LocalizationService(); l.Load("ru");
        var session = new VectorSession(files.SessionRoot); var item = files.Add(session);
        using var control = new VectorGenerationControl();
        control.Configure(l.T, new StorageSettings(), "fixture");
        typeof(VectorGenerationControl).GetField("_page", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(control, 2);
        typeof(VectorGenerationControl).GetMethod("Render", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)!.Invoke(control, null);
        Assert.IsFalse(ScenarioNavigationTests.LogicalDescendants(control).Any(x => AutomationProperties.GetAutomationId(x).StartsWith("Vector.History.", StringComparison.Ordinal)));
        control.ViewResult(item.Request.Id);
        Assert.IsTrue(ScenarioNavigationTests.LogicalDescendants(control).Any(x => AutomationProperties.GetAutomationId(x) == "Vector.History." + item.Request.Id));
        Assert.IsTrue(ScenarioNavigationTests.LogicalDescendants(control).Any(x => AutomationProperties.GetAutomationId(x) == "Vector.Export"));
        typeof(VectorGenerationControl).GetField("_busy", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(control, true);
        control.ClearWorkspace(); Assert.IsTrue(ScenarioNavigationTests.LogicalDescendants(control).Any(x => AutomationProperties.GetAutomationId(x) == "Vector.History." + item.Request.Id));
        typeof(VectorGenerationControl).GetField("_busy", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(control, false);
        control.ClearWorkspace();
        Assert.IsFalse(ScenarioNavigationTests.LogicalDescendants(control).Any(x => AutomationProperties.GetAutomationId(x).StartsWith("Vector.History.", StringComparison.Ordinal)));
        Assert.IsTrue(File.Exists(VectorStore.FilePath(item.Request.Id, "image.svg")));
    });

    [TestMethod, DataRow("ru", true), DataRow("ru", false), DataRow("en", true), DataRow("en", false)]
    public Task SettingsAndExportWindowsRenderAndApplyAtLargeScale(string language, bool dark) => ScenarioNavigationTests.Sta(() =>
    {
        var l = new LocalizationService(); l.Load(language);
        var owner = new Window();
        foreach (var key in new[] { "TextPrimaryBrush", "TextSecondaryBrush" }) owner.Resources[key] = dark ? Brushes.White : Brushes.Black;
        foreach (var key in new[] { "WindowBackgroundBrush", "PanelBrush", "SecondaryButtonBackgroundBrush", "SecondaryButtonHoverBrush" })
            owner.Resources[key] = dark ? new SolidColorBrush(Color.FromRgb(23, 32, 50)) : Brushes.WhiteSmoke;
        owner.Resources["LineBrush"] = Brushes.Gray; owner.Resources["AccentBrush"] = Brushes.RoyalBlue;
        owner.ShowInTaskbar = false; owner.WindowStartupLocation = WindowStartupLocation.Manual;
        owner.Left = -10000; owner.Top = -10000; owner.Width = 100; owner.Height = 100; owner.Show();
        try
        {
        var windows = new List<VectorDialog>();
        foreach (var section in new[] { "Canvas", "Style", "Detail", "Palette", "Background" })
            windows.Add(new VectorSettingsWindow(owner, l.T, new(), section));
        windows.Add(new VectorRasterExportWindow(owner, l.T, 1600, 900));
        foreach (var dialog in windows)
        {
            Exception? failure = null;
            var controls = ScenarioNavigationTests.LogicalDescendants(dialog).ToArray();
            void Select(string id, string value) => controls.OfType<ComboBox>().Single(x => AutomationProperties.GetAutomationId(x) == "Vector." + id).SelectedValue = value;
            void Enter(string id, string value) => controls.OfType<TextBox>().Single(x => AutomationProperties.GetAutomationId(x) == "Vector." + id).Text = value;
            switch (AutomationProperties.GetAutomationId(dialog))
            {
                case "Vector.Settings.Canvas": Enter("Settings.CanvasWidth", "21"); Enter("Settings.CanvasHeight", "9"); break;
                case "Vector.Settings.Style": Select("Settings.Purpose", "icon"); Select("Settings.Style", "flat"); break;
                case "Vector.Settings.Detail": Select("Settings.Detail", "detailed"); break;
                case "Vector.Settings.Palette": Select("Settings.Palette", "custom"); Enter("Settings.Colors", "#FF6600 #ABCDEF"); break;
                case "Vector.Settings.Background": Select("Settings.Background", "color"); Enter("Settings.BackgroundColor", "#336699"); break;
                case "Vector.Export.Window":
                    Select("Export.Format", "jpeg"); Enter("Export.Width", "1024");
                    Assert.AreEqual("576", controls.OfType<TextBox>().Single(x => AutomationProperties.GetAutomationId(x) == "Vector.Export.Height").Text);
                    controls.OfType<CheckBox>().Single().IsChecked = false; Enter("Export.Height", "512"); Enter("Export.Quality", "84"); break;
            }
            dialog.ShowInTaskbar = false; dialog.WindowStartupLocation = WindowStartupLocation.Manual; dialog.Left = -10000; dialog.Top = -10000;
            dialog.Loaded += (_, _) =>
            {
                try
                {
                dialog.ApplyTemplate(); dialog.UpdateLayout();
                var content = (FrameworkElement)dialog.Content;
                var png = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * 1.5), (int)Math.Ceiling(content.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
                var drawing = new DrawingVisual();
                using (var dc = drawing.RenderOpen())
                {
                    var bounds = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
                    dc.DrawRectangle(dialog.Background, null, bounds);
                    dc.DrawRectangle(new VisualBrush(content) { Stretch = Stretch.Fill }, null, bounds);
                }
                png.Render(drawing); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(png));
                var artifact = Path.Combine(Path.GetTempPath(), "lopata-vector-ui"); Directory.CreateDirectory(artifact);
                using (var output = File.Create(Path.Combine(artifact, language + "-" + dark + "-" + AutomationProperties.GetAutomationId(dialog) + ".png"))) encoder.Save(output);
                var buttons = ScenarioNavigationTests.LogicalDescendants(dialog).OfType<Button>().ToArray();
                var apply = buttons.Single(b => AutomationProperties.GetAutomationId(b) == "Vector.Dialog.Apply");
                apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                catch (Exception error) { failure = error; dialog.DialogResult = false; }
            };
            var accepted = dialog.ShowDialog();
            if (failure is not null) throw failure;
            Assert.AreEqual(true, accepted);
            var actions = ScenarioNavigationTests.LogicalDescendants(dialog).OfType<Button>().ToArray();
            Assert.IsTrue(actions.Single(b => AutomationProperties.GetAutomationId(b) == "Vector.Dialog.Apply").IsDefault);
            Assert.IsTrue(actions.Single(b => b.IsCancel).IsCancel);
            Assert.IsFalse(dialog.Title.StartsWith("Vector.", StringComparison.Ordinal));
            if (dialog is VectorRasterExportWindow export) Assert.AreEqual(new VectorRasterOptions(1024, 512, "jpeg", 84), export.Value);
            if (dialog is VectorSettingsWindow settings)
            {
                switch (AutomationProperties.GetAutomationId(dialog))
                {
                    case "Vector.Settings.Canvas": Assert.AreEqual(21, settings.Value.CanvasWidth); Assert.AreEqual(9, settings.Value.CanvasHeight); break;
                    case "Vector.Settings.Style": Assert.AreEqual("icon", settings.Value.Purpose); Assert.AreEqual("flat", settings.Value.Style); break;
                    case "Vector.Settings.Detail": Assert.AreEqual("detailed", settings.Value.Detail); break;
                    case "Vector.Settings.Palette": Assert.AreEqual("custom", settings.Value.Palette); CollectionAssert.AreEqual(new[] { "#FF6600", "#ABCDEF" }, settings.Value.ColorList); break;
                    case "Vector.Settings.Background": Assert.AreEqual("color", settings.Value.Background); Assert.AreEqual("#336699", settings.Value.BackgroundColor); break;
                }
            }
        }
        }
        finally { owner.Close(); }
    });

    internal sealed class Fixture : IDisposable
    {
        private readonly List<string> _ids = [];
        internal string SessionRoot { get; } = Path.Combine(Path.GetTempPath(), "lopata-vector-session-" + Guid.NewGuid().ToString("N"));
        internal VectorResult Add(VectorSession session)
        {
            var item = AddJob(session.Id); session.Add(item.Request); return item;
        }
        internal VectorResult AddLegacy() => AddJob(null);
        private VectorResult AddJob(string? session)
        {
            var id = Guid.NewGuid().ToString("N"); _ids.Add(id);
            var item = new VectorResult(new(id, "fixture", SessionRoot, null, DateTimeOffset.UtcNow, SessionId: session), "completed",
                Provenance: new() { Scenario = "vector-graphics/generation", ModelHistory = "recorded", Models = [new("recorded-model", "Fixture", "r1", "generation")] });
            VectorStore.Save(item);
            File.WriteAllText(VectorStore.FilePath(id, "image.svg"), "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'><rect x='49' width='1' height='100' fill='#00ff00'/></svg>");
            return item;
        }
        public void Dispose()
        {
            foreach (var id in _ids)
            {
                var path = Path.GetFullPath(VectorStore.Folder(id)); var root = Path.GetFullPath(VectorStore.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path) != id) throw new IOException("Unsafe fixture cleanup");
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
            if (Directory.Exists(SessionRoot)) Directory.Delete(SessionRoot, true);
        }
    }
}
