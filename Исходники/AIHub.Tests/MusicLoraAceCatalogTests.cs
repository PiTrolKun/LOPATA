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
public sealed class MusicLoraAceCatalogTests
{
    [TestMethod]
    public void NewAceChoicesSurviveProjectAndJobRoundTripWithTheirOriginalProvenance()
    {
        using var files = new MusicProjectTests.Files();
        var all = MusicLoraCatalog.For(MusicAceCatalog.Variation);
        Assert.HasCount(13, all); Assert.HasCount(12, all.Where(d => d.Format == "audio-sliders-peft-v1").ToArray());
        Assert.AreEqual("peft", MusicLoraCatalog.Find("jay2").Format);
        var selection = new MusicLoraSelection { ByModel = new() {
            [MusicAceCatalog.Variation] = all.Select(d => new MusicLoraChoice(d.Id, .4)).ToArray() } };
        var restored = JsonSerializer.Deserialize<MusicLoraSelection>(JsonSerializer.Serialize(selection))!;
        var uses = MusicLoraCatalog.Capture(restored, MusicAceCatalog.Variation);
        var jobs = new MusicGenerationJobs(Path.Combine(files.Root, "jobs"));
        var job = jobs.Create(files.Root, files.Root, "Song", 1, 8, "jazz", "Words", expert: MusicAceCatalog.Defaults(), loras: uses);
        CollectionAssert.AreEqual(uses, jobs.Load(job.Id).Loras);
        var tags = MusicSongMetadata.Create(job, job.Variants[0], 0);
        using var metadata = JsonDocument.Parse(tags["LOPATA_LORAS"]);
        Assert.AreEqual(13, metadata.RootElement.GetArrayLength());
        foreach (var d in all) {
            var item = metadata.RootElement.EnumerateArray().Single(x => x.GetProperty("Id").GetString() == d.Id);
            Assert.AreEqual(d.Format, item.GetProperty("Format").GetString());
            Assert.AreEqual(d.Files[0].Sha256, item.GetProperty("files")[0].GetProperty("Sha256").GetString());
        }
        Assert.IsFalse(tags["LOPATA_LORAS"].Contains(files.Root));
        Assert.HasCount(0, MusicLoraCatalog.Capture(restored with { Enabled = false }, MusicAceCatalog.Variation));
        Assert.HasCount(13, restored.Choices(MusicAceCatalog.Variation));
    }

    [TestMethod, DataRow("ru", true), DataRow("en", false)]
    public Task AceWindowShowsAndScrollsAllRealChoices(string language, bool dark) => ScenarioNavigationTests.Sta(() => {
        using var files = new MusicProjectTests.Files(); var localizer = new LocalizationService(); localizer.Load(language);
        var window = new MusicLoraWindow(null, files.Root, MusicAceCatalog.Variation, new(), "vintage bright jazz", "words",
            MusicAceCatalog.Defaults(), 8, localizer.T) { Width = 760, Height = 700, Left = -10000, Top = -10000, ShowInTaskbar = false };
        window.Resources["WindowBackgroundBrush"] = dark ? Brushes.DarkSlateGray : Brushes.WhiteSmoke;
        window.Resources["TextPrimaryBrush"] = dark ? Brushes.White : Brushes.Black;
        window.Resources["PanelBrush"] = dark ? Brushes.MidnightBlue : Brushes.White;
        window.Resources["LineBrush"] = Brushes.SlateGray; window.Resources["AccentBrush"] = Brushes.RoyalBlue;
        window.Resources["SecondaryButtonBackgroundBrush"] = dark ? Brushes.DarkSlateGray : Brushes.WhiteSmoke;
        try {
            window.Show(); window.UpdateLayout();
            var elements = ScenarioNavigationTests.LogicalDescendants(window).OfType<FrameworkElement>().ToArray();
            Assert.HasCount(13, elements.OfType<System.Windows.Controls.CheckBox>().Where(e => AutomationProperties.GetAutomationId(e).StartsWith("Music.Lora.Select.")).ToArray());
            var id = "ace-slider-vintage";
            elements.OfType<System.Windows.Controls.CheckBox>().Single(e => AutomationProperties.GetAutomationId(e) == "Music.Lora.Select." + id).IsChecked = true;
            elements.OfType<Slider>().Single(e => AutomationProperties.GetAutomationId(e) == "Music.Lora.Strength." + id).Value = .75;
            var scroll = elements.OfType<ScrollViewer>().Single(); scroll.ScrollToBottom(); window.UpdateLayout();
            Assert.IsTrue(scroll.ScrollableHeight > 0); Assert.AreEqual(scroll.ScrollableHeight, scroll.VerticalOffset, .1);
            Assert.IsTrue(scroll.ExtentWidth <= scroll.ViewportWidth + 1);
            Assert.AreEqual(.75, window.Selection.Choices(MusicAceCatalog.Variation).Single().Strength);
            var folder = Environment.GetEnvironmentVariable("LOPATA_LORA_UI_EVIDENCE");
            if (folder is not null) {
                Directory.CreateDirectory(folder); var content = (FrameworkElement)window.Content;
                var drawing = new DrawingVisual(); using (var canvas = drawing.RenderOpen()) {
                    canvas.DrawRectangle(dark ? Brushes.DarkSlateGray : Brushes.WhiteSmoke, null, new Rect(content.RenderSize));
                    canvas.DrawRectangle(new VisualBrush(content), null, new Rect(content.RenderSize));
                }
                var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(folder, "ace-catalog-" + language + ".png")); png.Save(stream);
            }
        } finally { window.Close(); }
    });
}
