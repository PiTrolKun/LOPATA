using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.MusicLora;
using CheckBox = System.Windows.Controls.CheckBox;
using Button = System.Windows.Controls.Button;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicLoraTests
{
    private static MusicLoraDefinition Jay => MusicLoraCatalog.Find("jay2");
    private static MusicLoraSelection Selected(double strength = .65) => new() { ByModel = new() {
        [MusicAceCatalog.Variation] = [new("jay2", strength)] } };
    [TestMethod]
    public void SelectionBypassDoesNotLoseStrengthAndSnapshotsDoNotShareBanks()
    {
        var original = Selected(); var clone = original.Snapshot(); clone.ByModel[MusicAceCatalog.Variation] = [new("jay2", .2)];
        Assert.AreEqual(.65, original.Choices(MusicAceCatalog.Variation).Single().Strength);
        var bypass = original with { Enabled = false }; Assert.HasCount(0, MusicLoraCatalog.Capture(bypass, MusicAceCatalog.Variation));
        Assert.HasCount(1, bypass.Choices(MusicAceCatalog.Variation));
        Assert.AreEqual(.65, MusicLoraCatalog.Capture(bypass with { Enabled = true }, MusicAceCatalog.Variation).Single().Strength);
        var disabled = original with { ByModel = new() { [MusicAceCatalog.Variation] = [new("jay2", .65) { Enabled = false }] } };
        Assert.HasCount(0, disabled.Choices(MusicAceCatalog.Variation));
        Assert.AreEqual(.65, JsonSerializer.Deserialize<MusicLoraSelection>(JsonSerializer.Serialize(disabled))!.ByModel[MusicAceCatalog.Variation][0].Strength);
        Assert.HasCount(0, MusicLoraCatalog.Capture(Selected(0), MusicAceCatalog.Variation));
    }
    [TestMethod]
    public void RequestsPinStrengthAndRejectWrongFamilyRevisionOrNonfiniteValues()
    {
        var u = new MusicLoraUse(Jay.Id, Jay.Revision, .5);
        MusicLoraCatalog.Validate([u], MusicAceCatalog.Variation);
        Assert.Throws<InvalidDataException>(() => MusicLoraCatalog.Validate([u], MusicStudioRuntime.Variation));
        Assert.Throws<InvalidDataException>(() => MusicLoraCatalog.Validate([u with { Revision = "other" }], MusicAceCatalog.Variation));
        Assert.Throws<InvalidDataException>(() => MusicLoraCatalog.Validate([u with { Strength = double.NaN }], MusicAceCatalog.Variation));
        Assert.Throws<InvalidDataException>(() => MusicLoraCatalog.Validate([u with { Strength = 1.1 }], MusicAceCatalog.Variation));
        var d = MusicLoraCatalog.Find("dreampop");
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(MusicStudioWorker.Request(new("soft", "words", 1, 2, 8) {
            Expert = MusicModelVariants.Defaults(MusicStudioRuntime.Variation), Loras = [new(d.Id, d.Revision, .35)] }, false, MusicStudioRuntime.Current)));
        Assert.AreEqual(.35, body.RootElement.GetProperty("adapters")[0].GetProperty("scales").GetProperty("ar").GetDouble());
        Assert.AreEqual(.35, body.RootElement.GetProperty("adapters")[0].GetProperty("scales").GetProperty("nar").GetDouble());
        Assert.AreEqual("sv_dreampop, soft", body.RootElement.GetProperty("style").GetString());
    }
    [TestMethod]
    public void JobsAndMetadataKeepActualSelectionWhileLegacyFilesDefaultToNoAdapters()
    {
        using var files = new MusicProjectTests.Files(); var jobs = new MusicGenerationJobs(Path.Combine(files.Root, "jobs"));
        var uses = MusicLoraCatalog.Capture(Selected(.8), MusicAceCatalog.Variation);
        var job = jobs.Create(files.Root, files.Root, "Song", 1, 10, "pop", "Words", expert: MusicAceCatalog.Defaults(), loras: uses);
        uses[0] = uses[0] with { Strength = .2 }; Assert.AreEqual(.8, jobs.Load(job.Id).Loras[0].Strength);
        var tags = MusicSongMetadata.Create(job, job.Variants[0], 0);
        Assert.Contains(Jay.Revision, tags["LOPATA_LORAS"]); Assert.Contains("0.8", tags["LOPATA_LORAS"]);
        Assert.IsFalse(tags["LOPATA_LORAS"].Contains(files.Root));
        var receipt = JsonSerializer.Serialize(new { initialization = new { project_root = files.Root, config_path = files.Root, device = "cuda" },
            loras = new[] { new { id = "jay2", strength = .8, directory = files.Root } } });
        var publicTags = MusicSongMetadata.Create(job, job.Variants[0] with { ExecutionReceipt = receipt }, 0);
        Assert.IsFalse(publicTags["LOPATA_ACE_REQUEST"].Contains(files.Root));
        Assert.Contains("cuda", publicTags["LOPATA_ACE_REQUEST"]); Assert.Contains("0.8", publicTags["LOPATA_ACE_REQUEST"]);
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(job))!.AsObject(); node.Remove("Loras");
        var old = JsonSerializer.Deserialize<MusicGenerationJob>(node.ToJsonString())!; Assert.HasCount(0, old.Loras); jobs.Save(old); Assert.HasCount(0, jobs.Load(old.Id).Loras);
        var snapshot = new MusicProjectSnapshot(); var projectJson = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(snapshot))!.AsObject(); projectJson.Remove("Loras");
        Assert.HasCount(0, JsonSerializer.Deserialize<MusicProjectSnapshot>(projectJson.ToJsonString())!.Loras.ByModel);
    }
    [TestMethod]
    public async Task RemovalOnlyDeletesCatalogFilesAndLeavesOtherDataAlone()
    {
        using var files = new MusicProjectTests.Files(); var d = Jay; var directory = d.Card(files.Root).InstallDirectory; Directory.CreateDirectory(directory);
        foreach (var f in d.Files) await File.WriteAllTextAsync(Path.Combine(directory, f.RelativePath), "test");
        var foreign = Path.Combine(directory, "user-notes.txt"); await File.WriteAllTextAsync(foreign, "keep");
        var companion = Path.Combine(files.Root, "nar_lora_joint_v9.safetensors"); await File.WriteAllTextAsync(companion, "keep");
        await MusicLoraAssets.RemoveAsync(d, files.Root, default, new(Path.Combine(files.Root, "store")));
        Assert.IsTrue(File.Exists(foreign)); Assert.IsTrue(File.Exists(companion)); Assert.IsTrue(d.Files.All(f => !File.Exists(Path.Combine(directory, f.RelativePath))));
    }
    [TestMethod]
    public void ForecastWarnsAboutFreeMemoryWithoutAttributingOtherAppsToLopata()
    {
        var uses = MusicLoraCatalog.Capture(Selected(), MusicAceCatalog.Variation);
        var estimate = MusicLoraForecast.Estimate(uses, 1_000_000_000, 900_000_000);
        Assert.AreEqual(false, estimate.GpuFits); Assert.IsTrue(estimate.AdditionalBytes > Jay.Bytes);
        Assert.AreEqual(true, MusicLoraForecast.Estimate([], 1_000_000_000, 900_000_000).GpuFits);
        Assert.IsNull(MusicLoraForecast.Estimate(uses, null, null).GpuFits);
        Assert.HasCount(0, MusicLoraCatalog.Reasons(Jay, "female pop voice"));
        Assert.Contains("male", MusicLoraCatalog.Reasons(Jay, "male pop voice"));
    }
    [TestMethod, DataRow("ru", true), DataRow("en", false)]
    public Task WindowStrengthAndCheckboxAreProjectScopedAndThemeAware(string language, bool dark) => ScenarioNavigationTests.Sta(() => {
        using var files = new MusicProjectTests.Files(); var localizer = new LocalizationService(); localizer.Load(language);
        var window = new MusicLoraWindow(null, files.Root, MusicAceCatalog.Variation, Selected(.65), "male pop", "words", MusicAceCatalog.Defaults(), 10, localizer.T);
        window.Resources["TextPrimaryBrush"] = dark ? Brushes.White : Brushes.Black;
        window.Resources["WindowBackgroundBrush"] = dark ? Brushes.DarkSlateGray : Brushes.WhiteSmoke;
        window.Resources["PanelBrush"] = dark ? new SolidColorBrush(Color.FromRgb(23, 32, 50)) : Brushes.White;
        window.Resources["LineBrush"] = Brushes.SlateGray; window.Resources["AccentBrush"] = Brushes.RoyalBlue;
        window.Resources["SecondaryButtonBackgroundBrush"] = dark ? Brushes.DarkSlateGray : Brushes.WhiteSmoke;
        window.Left = -10000; window.Top = -10000; window.ShowInTaskbar = false; window.Show(); window.UpdateLayout();
        try {
            var elements = ScenarioNavigationTests.LogicalDescendants(window).OfType<FrameworkElement>().ToArray();
            var slider = elements.OfType<Slider>().Single(s => AutomationProperties.GetAutomationId(s) == "Music.Lora.Strength.jay2");
            Assert.IsTrue(slider.IsEnabled); slider.Value = .4;
            var master = elements.OfType<CheckBox>().Single(c => AutomationProperties.GetAutomationId(c) == "Music.Lora.Master"); master.IsChecked = false;
            Assert.AreEqual(.4, window.Selection.Choices(MusicAceCatalog.Variation)[0].Strength); Assert.IsFalse(window.Selection.Enabled);
            master.IsChecked = true;
            var check = elements.OfType<CheckBox>().Single(c => AutomationProperties.GetAutomationId(c) == "Music.Lora.Select.jay2"); check.IsChecked = false;
            Assert.IsFalse(slider.IsEnabled); Assert.HasCount(0, window.Selection.Choices(MusicAceCatalog.Variation));
            check.IsChecked = true; Assert.AreEqual(.4, window.Selection.Choices(MusicAceCatalog.Variation)[0].Strength);
            window.UpdateLayout();
            var folder = Environment.GetEnvironmentVariable("LOPATA_LORA_UI_EVIDENCE"); if (folder is not null) {
                Directory.CreateDirectory(folder); var content = (FrameworkElement)window.Content;
                var drawing = new DrawingVisual(); using (var canvas = drawing.RenderOpen()) {
                    var rect = new Rect(content.RenderSize); canvas.DrawRectangle(dark ? Brushes.DarkSlateGray : Brushes.WhiteSmoke, null, rect);
                    canvas.DrawRectangle(new VisualBrush(content), null, rect);
                }
                var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(Path.Combine(folder, "lora-" + language + ".png")); png.Save(output);
            }
        } finally { window.Close(); }
    });

    [TestMethod]
    public Task ProjectsRestoreTheirOwnAdapterBankAndNewProjectsStartEmpty() => ScenarioNavigationTests.Sta(() => {
        using var files = new MusicProjectTests.Files();
        var projects = new MusicProjects(Path.Combine(files.Root, "projects"));
        var jobs = new MusicGenerationJobs(Path.Combine(files.Root, "jobs"));
        using var view = new MusicWorkspaceControl(projects, jobs, new MusicOutputPreferences(Path.Combine(files.Root, "output.json")));
        view.ConfigureOutput(files.Root, _ => { });
        view.Generation.ConfigureVariation(MusicAceCatalog.Variation, MusicAceCatalog.Defaults());
        view.Lora.Restore(Selected(.8) with { Enabled = false }); view.Projects.Fix(); var id = view.Projects.Current.Id;
        view.Projects.New(); Assert.HasCount(0, view.Lora.Selection.ByModel);
        view.Projects.Open(id); Assert.IsFalse(view.Lora.Selection.Enabled);
        Assert.AreEqual(.8, view.Lora.Selection.Choices(MusicAceCatalog.Variation)[0].Strength);
        view.Projects.SwitchModel(MusicModelVariants.Bf16); Assert.HasCount(0, view.Lora.Selection.Choices(MusicModelVariants.Bf16));
        view.Projects.SwitchModel(MusicAceCatalog.Variation);
        Assert.AreEqual(.8, view.Lora.Selection.Choices(MusicAceCatalog.Variation)[0].Strength);
    });
}
