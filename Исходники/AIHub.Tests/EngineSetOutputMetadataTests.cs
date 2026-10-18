using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using AIHub.Models;
using AIHub.Services;
using SkiaSharp;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class EngineSetOutputMetadataTests
{
    private string _root = "", _active = "", _recommended = "";
    private Func<IReadOnlyList<string>, CancellationToken, Task>? _licenseGate;

    [TestInitialize]
    public void Setup()
    {
        _root = MusicOutputTests.Temporary();
        _active = EngineSetSelection.ActiveId; _recommended = EngineSetSelection.RecommendedId;
        _licenseGate = ComponentLicenseGate.ConfirmAsync;
        ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
    }

    [TestCleanup]
    public void Cleanup()
    {
        EngineSetSelection.Publish(_active, _recommended);
        ComponentLicenseGate.ConfirmAsync = _licenseGate;
        Directory.Delete(_root, true);
    }

    [TestMethod]
    [DataRow(MusicComponentCatalog.ModelId)]
    [DataRow(MusicStudioRuntime.Variation)]
    [DataRow(MusicModelVariants.Bf16)]
    [DataRow(MusicAceCatalog.Variation)]
    [DataRow(MusicDiffRhythmCatalog.Variation)]
    [DataRow(MusicHeartMuLaCatalog.Variation)]
    public void EveryMusicModelPinsItsCreationSetAcrossASelectionChange(string variation)
    {
        EngineSetSelection.Publish("001", "002");
        var jobs = new MusicGenerationJobs(Path.Combine(_root, "jobs"));
        var job = jobs.Create(_root, _root, "Песня", 1, 8, "rock", "Текст",
            expert: MusicModelVariants.Defaults(variation)) with { AppVersion = "0.5.78-dev" };
        jobs.Save(job);
        EngineSetSelection.Publish("002", "002");
        var restored = jobs.Load(job.Id);
        var tags = MusicSongMetadata.Create(restored, restored.Variants[0], 0);
        Assert.AreEqual("001", restored.EngineSetId);
        Assert.AreEqual("001", tags["LOPATA_ENGINE_SET"]);
        Assert.AreEqual("2026-10-17", tags["LOPATA_ENGINE_SET_DATE"]);
        Assert.AreEqual("LOPATA 0.5.78-dev; engine set 001 (2026-10-17)", tags["producer"]);
        Assert.AreEqual("0.5.78-dev", tags["LOPATA_VERSION"]);
    }

    [TestMethod]
    public void OldJobsWithoutTheNewFieldKeepUnknownHistoryOrTheirKnownStudioProfile()
    {
        EngineSetSelection.Publish("002", "002");
        var jobs = new MusicGenerationJobs(Path.Combine(_root, "jobs"));
        foreach (var variation in new[] { MusicComponentCatalog.ModelId, MusicStudioRuntime.Variation })
        {
            var job = jobs.Create(_root, _root, "Старая песня", 1, 8, "rock", "Текст",
                expert: MusicModelVariants.Defaults(variation));
            if (variation == MusicStudioRuntime.Variation)
                job = job with { RuntimeRevision = MusicStudioRuntime.Legacy.Revision, RuntimePack = MusicStudioRuntime.Legacy.Pack };
            var old = JsonNode.Parse(JsonSerializer.Serialize(job))!.AsObject(); old.Remove("EngineSetId");
            File.WriteAllText(Path.Combine(jobs.Folder(job.Id), "job.json"), old.ToJsonString());
            var restored = jobs.Load(job.Id); Assert.IsNull(restored.EngineSetId);
            var tags = MusicSongMetadata.Create(restored, restored.Variants[0], 0);
            Assert.AreEqual(variation == MusicStudioRuntime.Variation ? "001" : "unknown", tags["LOPATA_ENGINE_SET"]);
            StringAssert.Contains(tags["producer"], variation == MusicStudioRuntime.Variation ? "engine set 001" : "engine set unknown");
            Assert.AreEqual(variation == MusicStudioRuntime.Variation, tags.ContainsKey("LOPATA_ENGINE_SET_DATE"));
        }
    }

    [TestMethod]
    public void StudioRuntimeContractWinsOverAConflictingJobSnapshot()
    {
        var jobs = new MusicGenerationJobs(Path.Combine(_root, "jobs"));
        var job = jobs.Create(_root, _root, "Песня", 1, 8, "rock", "Текст",
            expert: MusicModelVariants.Defaults(MusicStudioRuntime.Variation)) with {
                EngineSetId = "002", RuntimeRevision = MusicStudioRuntime.Legacy.Revision, RuntimePack = MusicStudioRuntime.Legacy.Pack };
        var tags = MusicSongMetadata.Create(job, job.Variants[0], 0);
        Assert.AreEqual("001", tags["LOPATA_ENGINE_SET"]);
        StringAssert.Contains(tags["producer"], "engine set 001 (2026-10-17)");
        Assert.AreEqual("runtime-profile; conflicting-job-snapshot", tags["LOPATA_ENGINE_SET_HISTORY"]);
    }

    [TestMethod]
    public async Task EveryAudioWriterKeepsTheVisibleProducerAndMachineIdentityFromTheSavedJob()
    {
        EngineSetSelection.Publish("001", "002");
        var jobs = new MusicGenerationJobs(Path.Combine(_root, "jobs"));
        var job = jobs.Create(_root, _root, "Песня", 1, 8, "rock", "Текст",
            expert: MusicModelVariants.Defaults(MusicStudioRuntime.Variation)) with { AppVersion = "0.5.78-dev" };
        jobs.Save(job); EngineSetSelection.Publish("002", "002");
        var saved = jobs.Load(job.Id); var tags = MusicSongMetadata.Create(saved, saved.Variants[0], 0);
        var wave = Path.Combine(_root, "source.wav"); MusicOutputTests.WriteWave(wave);
        foreach (var format in Enum.GetValues<MusicAudioFormat>())
        {
            var path = Path.Combine(_root, "metadata-" + format + MusicOutputSettings.Extension(format));
            await MusicAudioEncoder.Default.EncodeAsync(wave, path, format, 160, tags, default);
            var read = await AudioConversionMetadata.ReadAsync(MusicAudioRuntime.Default, path, default);
            Assert.AreEqual(tags["producer"], read.Tags["producer"], format.ToString());
            Assert.AreEqual("001", read.Tags["LOPATA_ENGINE_SET"], format.ToString());
            Assert.AreEqual("2026-10-17", read.Tags["LOPATA_ENGINE_SET_DATE"], format.ToString());
            Assert.AreEqual("0.5.78-dev", read.Tags["LOPATA_VERSION"], format.ToString());
        }
    }

    [TestMethod]
    public async Task PngAndItsResizedCopyKeepTheExecutedReceiptInVisibleAndMachineFields()
    {
        EngineSetSelection.Publish("002", "002");
        var request = new ImageGenerationRequest(Guid.NewGuid().ToString("N"), "z-image", "Кот у монитора", 32, 32,
            [123], "PRIVATE", _root) { Metadata = new("Автор"), EngineSetId = "002", OutputLongestSide = 1280 };
        var receipt = new ImageGenerationRuntimeReceipt("001", "3f8527a-lopata1",
            NativeRuntimeReleasePolicy.LegacySdRevision, "cpu", "runtime.sd.cpu");
        var fields = ImageGenerationMetadata.Create(request, 0, DateTimeOffset.UtcNow, receipt);
        var source = Path.Combine(_root, "source.png");
        using (var bitmap = new SKBitmap(32, 32))
        {
            bitmap.Erase(SKColors.Coral); using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100); File.WriteAllBytes(source, data.ToArray());
        }
        PngMetadataWriter.Write(source, fields);
        var resized = Path.Combine(_root, "resized.png");
        ImageRasterScaler.SavePng(source, resized, 64, 64,
            ImageGenerationMetadata.ForOutput(PngTextMetadata.Read(source), request, 64, 64, "Mitchell"), default);
        foreach (var path in new[] { source, resized })
        {
            var actual = PngTextMetadata.Read(path);
            Assert.AreEqual("001", actual["LOPATA_ENGINE_SET"]);
            Assert.AreEqual("2026-10-17", actual["LOPATA_ENGINE_SET_DATE"]);
            using var json = JsonDocument.Parse(actual["LOPATA"]);
            Assert.AreEqual("001", json.RootElement.GetProperty("EngineSetId").GetString());
            Assert.AreEqual("2026-10-17", json.RootElement.GetProperty("EngineSetDate").GetString());
            Assert.AreEqual("001", json.RootElement.GetProperty("Runtime").GetProperty("EngineSetId").GetString());
            var producer = "LOPATA " + json.RootElement.GetProperty("ProgramVersion").GetString() + "; engine set 001 (2026-10-17)";
            Assert.AreEqual(producer, actual["Software"]);
            XNamespace xmp = "http://ns.adobe.com/xap/1.0/";
            Assert.AreEqual(producer, XElement.Parse(actual["XML:com.adobe.xmp"]).Descendants(xmp + "CreatorTool").Single().Value);
            var independent = await FileMetadataExifTool.ReadAsync(path, default);
            Assert.IsTrue(independent.EnumerateObject().Any(p => p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() == producer));
        }
        var unknown = ImageGenerationMetadata.Create(request, 0, DateTimeOffset.UtcNow);
        Assert.AreEqual("unknown", unknown["LOPATA_ENGINE_SET"]);
        Assert.IsFalse(unknown.ContainsKey("LOPATA_ENGINE_SET_DATE"));
        StringAssert.Contains(unknown["Software"], "engine set unknown");
    }
}
