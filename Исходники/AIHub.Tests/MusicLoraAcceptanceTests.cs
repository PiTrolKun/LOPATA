using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AIHub.Controls;
using AIHub.Services;
using AIHub.Services.MusicLora;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicLoraAcceptanceTests
{
    [TestMethod]
    public Task RightClickKeepsBothModelBanksAndSubmittedHistoryAcrossProjectSwitches() => ScenarioNavigationTests.Sta(() => {
        using var files = new MusicProjectTests.Files();
        var projects = new MusicProjects(Path.Combine(files.Root, "projects"));
        var jobs = new MusicGenerationJobs(Path.Combine(files.Root, "jobs"));
        var originalPreparation = MusicLoraAssets.RequestPreparation;
        using var view = new MusicWorkspaceControl(projects, jobs, new(Path.Combine(files.Root, "output.json")));
        try {
            view.ConfigureOutput(files.Root, _ => { }); view.Lora.Configure(files.Root);
            view.Generation.ConfigureVariation(MusicAceCatalog.Variation, MusicAceCatalog.Defaults());
            var bank = new MusicLoraSelection { ByModel = new() {
                [MusicAceCatalog.Variation] = [new("jay2", .8), new("ace-slider-energy", .55)],
                [MusicStudioRuntime.Variation] = [new("ntc-metal", .45)] } };
            view.Lora.Restore(bank); view.Projects.Fix(); var id = view.Projects.Current.Id;
            var uses = MusicLoraCatalog.Capture(bank, MusicAceCatalog.Variation);
            var job = jobs.Create(files.Root, files.Root, "Stand", 1, 8, "pop", "Words", expert: MusicAceCatalog.Defaults(), loras: uses);
            view.Projects.Record(job, view.Projects.Capture());
            var button = view.Generation.LoraButton;
            void RightClick() => button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) {
                RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent });
            string Count() => ((Grid)button.Content).Children.OfType<TextBlock>().Single().Text;
            Assert.AreEqual("2", Count()); RightClick();
            Assert.IsFalse(view.Lora.Selection.Enabled); Assert.IsTrue(button.Opacity < 1); Assert.IsTrue(button.IsEnabled);
            Assert.AreEqual("2", Count()); Assert.HasCount(0, MusicLoraCatalog.Capture(view.Lora.Selection, MusicAceCatalog.Variation));
            CollectionAssert.AreEqual(uses, jobs.Load(job.Id).Loras);
            Assert.IsTrue(projects.Load(id).Steps[0].Snapshot.Loras.Enabled);
            Assert.IsFalse(projects.Load(id).Saved.Loras.Enabled);
            view.Projects.SwitchModel(MusicStudioRuntime.Variation); Assert.AreEqual("1", Count());
            Assert.AreEqual(.45, view.Lora.Selection.Choices(MusicStudioRuntime.Variation).Single().Strength);
            view.Projects.SwitchModel(MusicAceCatalog.Variation); Assert.AreEqual("2", Count());
            view.Projects.New(); Assert.HasCount(0, view.Lora.Selection.ByModel);
            view.Projects.Open(id); Assert.IsFalse(view.Lora.Selection.Enabled); Assert.AreEqual("2", Count());
            RightClick(); Assert.IsTrue(view.Lora.Selection.Enabled); Assert.AreEqual(1d, button.Opacity);
            CollectionAssert.AreEqual(uses, MusicLoraCatalog.Capture(view.Lora.Selection, MusicAceCatalog.Variation));
            CollectionAssert.AreEqual(uses, jobs.Load(job.Id).Loras);
        } finally { MusicLoraAssets.RequestPreparation = originalPreparation; }
    });

    [TestMethod, DataRow(false), DataRow(true)]
    public async Task MissingAdaptersNeverBecomeSuccessfulAfterDeclinedOrIncompletePreparation(bool claimedSuccess)
    {
        using var files = new MusicProjectTests.Files();
        var originalPreparation = MusicLoraAssets.RequestPreparation; var originalLicense = ComponentLicenseGate.ConfirmAsync;
        var definition = MusicLoraCatalog.Find("ace-slider-energy");
        var uses = new[] { new MusicLoraUse(definition.Id, definition.Revision, .6) };
        var calls = 0;
        ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
        MusicLoraAssets.RequestPreparation = (root, requested, _) => {
            Assert.AreEqual(files.Root, root); CollectionAssert.AreEqual(uses, requested); calls++;
            return Task.FromResult(claimedSuccess);
        };
        try {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => MusicLoraAssets.EnsureAsync(files.Root, uses, MusicAceCatalog.Variation, default));
            Assert.AreEqual("Music.Lora.Missing", error.Message); Assert.AreEqual(1, calls);
            Assert.IsFalse(Directory.Exists(Path.Combine(files.Root, "MusicLoRA"))); Assert.AreEqual(.6, uses[0].Strength);
        } finally { MusicLoraAssets.RequestPreparation = originalPreparation; ComponentLicenseGate.ConfirmAsync = originalLicense; }
    }

    [TestMethod]
    public async Task CancellationDuringMissingAdapterPreparationStopsTheOperation()
    {
        using var files = new MusicProjectTests.Files(); using var cancellation = new CancellationTokenSource();
        var originalPreparation = MusicLoraAssets.RequestPreparation; var originalLicense = ComponentLicenseGate.ConfirmAsync;
        var definition = MusicLoraCatalog.Find("ace-slider-vintage");
        var uses = new[] { new MusicLoraUse(definition.Id, definition.Revision, .7) };
        ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
        MusicLoraAssets.RequestPreparation = (_, _, token) => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.FromResult(true); };
        try {
            await Assert.ThrowsAsync<OperationCanceledException>(() => MusicLoraAssets.EnsureAsync(files.Root, uses, MusicAceCatalog.Variation, cancellation.Token));
            Assert.IsFalse(Directory.Exists(Path.Combine(files.Root, "MusicLoRA"))); Assert.AreEqual(.7, uses[0].Strength);
        } finally { MusicLoraAssets.RequestPreparation = originalPreparation; ComponentLicenseGate.ConfirmAsync = originalLicense; }
    }

    [TestMethod]
    public async Task ExistingAceAudioExportsAllActualAdapterAndConversionDetailsInEveryFormat()
    {
        var folder = Environment.GetEnvironmentVariable("LOPATA_LORA_ACCEPTANCE");
        var source = Environment.GetEnvironmentVariable("LOPATA_LORA_ACCEPTANCE_SOURCE");
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(source)) { Assert.Inconclusive("Explicit export stand with an existing ACE receipt only."); return; }
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var token = deadline.Token;
        using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(source + ".receipt.json", token));
        var actual = receipt.RootElement.GetProperty("loras");
        var uses = actual.EnumerateArray().Select(item => new MusicLoraUse(item.GetProperty("id").GetString()!,
            item.GetProperty("revision").GetString()!, item.GetProperty("strength").GetDouble())).ToArray();
        Assert.HasCount(13, uses);
        var jobs = new MusicGenerationJobs(Path.Combine(folder, "jobs"));
        var original = receipt.RootElement.GetProperty("request").GetProperty("params");
        var job = jobs.Create(folder, folder, "ACE LoRA verification export", 1, 8,
            original.GetProperty("caption").GetString()!, original.GetProperty("lyrics").GetString()!,
            expert: MusicAceCatalog.Defaults(), loras: uses);
        var variant = job.Variants[0] with { ExecutionReceipt = receipt.RootElement.GetRawText(), SoundSeed = original.GetProperty("seed").GetInt32() };
        var tags = MusicSongMetadata.Create(job, variant, 0);
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source, token)));
        foreach (var format in Enum.GetValues<MusicAudioFormat>()) {
            var output = Path.Combine(folder, "actual-ace" + MusicOutputSettings.Extension(format));
            await MusicAudioEncoder.Default.EncodeAsync(source, output, format, 160, tags, token);
            var raw = await MusicAudioRuntime.Default.RunAsync(true, ["-v", "error", "-show_format", "-show_streams", "-of", "json", output], token);
            await File.WriteAllTextAsync(output + ".ffprobe.json", raw, token);
            using var probe = JsonDocument.Parse(raw);
            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Collect(probe.RootElement.GetProperty("format"));
            foreach (var stream in probe.RootElement.GetProperty("streams").EnumerateArray()) Collect(stream);
            Assert.AreEqual(tags["LOPATA_LORAS"], found["LOPATA_LORAS"]);
            Assert.AreEqual(tags["LOPATA_ACE_REQUEST"], found["LOPATA_ACE_REQUEST"]);
            Assert.AreEqual(tags["LOPATA_VERSION"], found["LOPATA_VERSION"]);
            using var exported = JsonDocument.Parse(found["LOPATA_ACE_REQUEST"]);
            foreach (var item in exported.RootElement.GetProperty("loras").EnumerateArray()) {
                Assert.IsFalse(item.TryGetProperty("directory", out _));
                if (item.GetProperty("id").GetString() != "jay2") Assert.AreEqual(704, item.GetProperty("adaptation").GetProperty("verified_tensors").GetInt32());
            }
            Console.WriteLine("Actual ACE metadata verified by ffprobe: " + format);
            void Collect(JsonElement element) {
                if (element.TryGetProperty("tags", out var values)) foreach (var item in values.EnumerateObject()) found[item.Name] = item.Value.GetString()!;
            }
        }
        Assert.AreEqual(hash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source, token))));
        await File.WriteAllTextAsync(Path.Combine(folder, "export-pass.json"), JsonSerializer.Serialize(new { pass = true, formats = 4, actualAdapters = uses.Length, sourceUnchanged = true }), token);
    }
}
