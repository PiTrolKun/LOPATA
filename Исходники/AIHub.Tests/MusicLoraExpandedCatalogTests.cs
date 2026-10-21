using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Controls;
using AIHub.Services;
using AIHub.Services.MusicLora;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicLoraExpandedCatalogTests
{
    [TestMethod]
    public async Task BulkDownloadAsksForTheWholeSelectionBeforeTouchingFiles()
    {
        using var files = new MusicProjectTests.Files(); var previous = ComponentLicenseGate.ConfirmAsync;
        var asked = new List<string[]>();
        ComponentLicenseGate.ConfirmAsync = (ids, _) => { asked.Add(ids.ToArray()); throw new OperationCanceledException("Stand: consent declined."); };
        try {
            var uses = new[] { "ntc-metal", "instrumental-v3" }.Select(id => {
                var d = MusicLoraCatalog.Find(id); return new MusicLoraUse(id, d.Revision, d.Default);
            }).ToArray();
            await Assert.ThrowsAsync<OperationCanceledException>(() => MusicLoraAssets.PrepareAsync(files.Root, uses, true, null, default));
            Assert.HasCount(1, asked); CollectionAssert.AreEquivalent(new[] { "music-lora-ntc-metal", "music-lora-instrumental-v3" }, asked.Single());
            Assert.IsFalse(Directory.Exists(Path.Combine(files.Root, "MusicLoRA")));
        } finally { ComponentLicenseGate.ConfirmAsync = previous; }
    }

    [TestMethod]
    public void EveryBundledAdapterHasPinnedLicenseTermsAndAnExecutableRecipe()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Licenses");
        var license = new ComponentLicenseService(path, Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
        foreach (var d in MusicLoraCatalog.Bundled) {
            var entry = license.Entries.Single(e => e.Id == d.LicenseId);
            Assert.AreEqual(d.Revision, entry.Version); Assert.AreEqual(d.License, entry.License);
            Assert.IsFalse(entry.Basic); Assert.AreEqual("download", entry.Delivery);
            Assert.IsTrue(entry.Texts.All(f => File.Exists(Path.Combine(path, f))));
            Assert.IsTrue(d.Slots.Count > 0 && d.Slots.All(s => new[] { "ar", "nar", "dit" }.Contains(s.Key) && double.IsFinite(s.Value) && s.Value > 0));
            Assert.IsTrue(d.Files.All(f => f.SourceUrl.Contains("/resolve/" + d.Revision + "/")));
        }
        Assert.HasCount(16, MusicLoraCatalog.All.Where(d => d.Id.StartsWith("ntc-")).ToArray());
        Assert.HasCount(6, MusicLoraCatalog.All.Where(d => d.Id.StartsWith("grvl-")).ToArray());
        Assert.HasCount(6, MusicLoraCatalog.All.Where(d => d.Id.StartsWith("mltnt-")).ToArray());
    }

    [TestMethod]
    public void FullExpandedBankFitsProjectCapacityAndKeepsStageRatiosAndTriggers()
    {
        var previous = EngineSetSelection.ActiveId; var recommended = EngineSetSelection.RecommendedId;
        try {
            EngineSetSelection.Publish("002", recommended);
            var all = MusicLoraCatalog.Bundled.Where(d => d.Variation == MusicStudioRuntime.Variation).ToArray(); Assert.HasCount(32, all);
            var selection = new MusicLoraSelection { ByModel = new() {
                [MusicStudioRuntime.Variation] = all.Select(d => new MusicLoraChoice(d.Id, .6)).ToArray() } };
            var restored = JsonSerializer.Deserialize<MusicLoraSelection>(JsonSerializer.Serialize(selection))!;
            restored.Validate(); var uses = MusicLoraCatalog.Capture(restored, MusicStudioRuntime.Variation); Assert.HasCount(32, uses);
            var style = MusicLoraCatalog.EffectiveStyle("warm piano", uses);
            Assert.AreEqual(1, style.Split(", ").Count(x => x == "grvl")); Assert.AreEqual(1, style.Split(", ").Count(x => x == "mltnt"));
            var chanter = MusicLoraCatalog.Find("mltnt-chanter");
            using var body = JsonDocument.Parse(JsonSerializer.Serialize(MusicStudioWorker.Request(new("reggae", "Words", 1, 2, 8) {
                Expert = MusicModelVariants.Defaults(MusicStudioRuntime.Variation), Loras = [new(chanter.Id, chanter.Revision, .6)] }, false, MusicStudioRuntime.Current)));
            var scales = body.RootElement.GetProperty("adapters")[0].GetProperty("scales");
            Assert.AreEqual(.3, scales.GetProperty("ar").GetDouble()); Assert.AreEqual(.6, scales.GetProperty("nar").GetDouble());
            Assert.IsFalse(MusicLoraCatalog.Find("ntc-metal").Slots.ContainsKey("nar"));
            Assert.HasCount(0, MusicLoraCatalog.Reasons(MusicLoraCatalog.Find("ntc-male"), "female"));
            EngineSetSelection.Publish("001", recommended); Assert.HasCount(0, MusicLoraCatalog.For(MusicStudioRuntime.Variation));
        } finally { EngineSetSelection.Publish(previous, recommended); }
    }

    [TestMethod, DataRow("ru", true), DataRow("en", false)]
    public Task LongCatalogScrollsToLastCardWithoutHorizontalOverflow(string language, bool dark) => ScenarioNavigationTests.Sta(() => {
        var previous = EngineSetSelection.ActiveId; var recommended = EngineSetSelection.RecommendedId;
        using var files = new MusicProjectTests.Files(); var localizer = new LocalizationService(); localizer.Load(language);
        EngineSetSelection.Publish("002", recommended);
        var window = new MusicLoraWindow(null, files.Root, MusicStudioRuntime.Variation, new(), "female metal", "words",
            MusicModelVariants.Defaults(MusicStudioRuntime.Variation), 8, localizer.T) {
                Width = 760, Left = -10000, Top = -10000, ShowInTaskbar = false };
        window.Resources["WindowBackgroundBrush"] = dark ? Brushes.DarkSlateGray : Brushes.WhiteSmoke;
        window.Resources["TextPrimaryBrush"] = dark ? Brushes.White : Brushes.Black;
        window.Resources["PanelBrush"] = dark ? Brushes.MidnightBlue : Brushes.White;
        window.Resources["LineBrush"] = Brushes.SlateGray; window.Resources["AccentBrush"] = Brushes.RoyalBlue;
        window.Resources["SecondaryButtonBackgroundBrush"] = dark ? Brushes.DarkSlateGray : Brushes.WhiteSmoke;
        try {
            window.Show(); window.UpdateLayout();
            var elements = ScenarioNavigationTests.LogicalDescendants(window).OfType<FrameworkElement>().ToArray();
            Assert.HasCount(MusicLoraCatalog.For(MusicStudioRuntime.Variation).Length,
                elements.OfType<System.Windows.Controls.CheckBox>().Where(e => AutomationProperties.GetAutomationId(e).StartsWith("Music.Lora.Select.")).ToArray());
            var lastId = MusicLoraCatalog.For(MusicStudioRuntime.Variation)[^1].Id;
            elements.OfType<System.Windows.Controls.CheckBox>().Single(e => AutomationProperties.GetAutomationId(e) == "Music.Lora.Select." + lastId).IsChecked = true;
            var scroll = elements.OfType<ScrollViewer>().Single(); scroll.ScrollToBottom(); window.UpdateLayout();
            Assert.IsTrue(scroll.ScrollableHeight > 0); Assert.AreEqual(scroll.ScrollableHeight, scroll.VerticalOffset, .1);
            Assert.IsTrue(scroll.ExtentWidth <= scroll.ViewportWidth + 1);
            Assert.AreEqual(lastId, window.Selection.Choices(MusicStudioRuntime.Variation).Single().Id);
            var folder = Environment.GetEnvironmentVariable("LOPATA_LORA_UI_EVIDENCE");
            if (folder is not null) {
                Directory.CreateDirectory(folder); var content = (FrameworkElement)window.Content;
                var drawing = new DrawingVisual(); using (var canvas = drawing.RenderOpen()) {
                    var area = new Rect(content.RenderSize); canvas.DrawRectangle(dark ? Brushes.DarkSlateGray : Brushes.WhiteSmoke, null, area);
                    canvas.DrawRectangle(new VisualBrush(content), null, area);
                }
                var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(drawing); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(folder, "expanded-" + language + ".png")); png.Save(stream);
            }
        } finally { window.Close(); EngineSetSelection.Publish(previous, recommended); }
    });
}
