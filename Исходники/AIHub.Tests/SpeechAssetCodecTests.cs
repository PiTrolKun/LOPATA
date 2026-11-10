using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.SpeechAssets;
using AIHub.Services.VideoEditor;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class SpeechAssetCodecTests
{
    [TestMethod]
    public async Task ExclusiveSelectionKeepsBothTakesAndExportsOnlyTheChosenId()
    {
        if (Environment.GetEnvironmentVariable("LOPATA_SPEECH_ASSETS_CODECS") != "1") Assert.Inconclusive("Explicit installed-codec test required.");
        var root = Path.Combine(AppDataPaths.ProjectRoot!, "Тесты", "SpeechAssets_20261109", "marker-multiple");
        Directory.CreateDirectory(root); var store = new SpeechAssetStore(Path.Combine(root, "sessions"));
        var session = new SpeechAssetSession { Rows = SpeechAssetMarkdown.Parse("### 0001 · voice.00ea4a3ee4914c14\n- Test line") };
        using var sourceZip = ZipFile.OpenRead(Path.Combine(AppDataPaths.ProjectRoot!, "Тесты", "SpeechAssets_20261109", "installed", "en-technical-probe.zip"));
        foreach (var index in new[] { 1, 2 })
        {
            var take = new SpeechAssetTake { Id = Guid.NewGuid().ToString("N"), File = $"audio/{index}.wav", Selected = true,
                Text = session.Rows[0].Text, Voice = session.Voice with { Language = "en" } };
            var path = store.FileFor(session.Id, take.File); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            sourceZip.Entries.Single(e => e.FullName.EndsWith(".wav")).ExtractToFile(path, true);
            take.Sha256 = SpeechAssetStore.Hash(path); session.Rows[0].Takes.Add(take);
        }
        Assert.Throws<InvalidDataException>(() => session.Rows[0].Confirm());
        session.Rows[0].SelectTake(session.Rows[0].Takes[1].Id);
        session.Rows[0].Confirm(); store.Save(session); var zipPath = Path.Combine(root, "exclusive.zip");
        await store.ExportAsync(session, zipPath, default, format: MusicAudioFormat.Wav);
        using var zip = ZipFile.OpenRead(zipPath);
        Assert.IsNotNull(zip.GetEntry("audio/0001_voice.00ea4a3ee4914c14.wav"));
        using var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open());
        var items = manifest.RootElement.GetProperty("Items").EnumerateArray().ToArray();
        Assert.HasCount(1, items); Assert.AreEqual(session.Rows[0].Takes[1].Id, items[0].GetProperty("TakeId").GetString());
        Assert.IsTrue(File.Exists(store.FileFor(session.Id, session.Rows[0].Takes[0].File)), "The unselected source is retained.");
        foreach (var item in items)
        {
            var path = Path.Combine(root, Path.GetFileName(item.GetProperty("Name").GetString())!);
            zip.GetEntry(item.GetProperty("Name").GetString()!)!.ExtractToFile(path, true);
            var info = await AudioConversionMetadata.ReadAsync(MusicAudioRuntime.Default, path, default);
            using var marker = JsonDocument.Parse(info.Tags["LOPATA_SPEECH_ASSET"]);
            Assert.AreEqual(item.GetProperty("TakeId").GetString(), marker.RootElement.GetProperty("TakeId").GetString());
        }
    }
    [TestMethod, DataRow(false), DataRow(true)]
    public async Task ExistingSpeechExportsFourFormatsWithValidMetadataAndHashes(bool audit)
    {
        if (Environment.GetEnvironmentVariable("LOPATA_SPEECH_ASSETS_CODECS") != "1") Assert.Inconclusive("Explicit installed-codec test required.");
        var root = Path.Combine(AppDataPaths.ProjectRoot!, "Тесты", "SpeechAssets_20261109", "projects-codecs", audit ? "audit-on" : "audit-off");
        Directory.CreateDirectory(root);
        using var auditStore = ComponentAudit.UseTestStore(Path.Combine(root, "audit"), audit);
        var store = new SpeechAssetStore(Path.Combine(root, "sessions"));
        var session = new SpeechAssetSession { SourceName = "codec-probe.md", Rows = SpeechAssetMarkdown.Parse("- Technical speech probe") };
        session.Rows[0].Group = "0001 · voice.00ea4a3ee4914c14";
        using var original = ZipFile.OpenRead(Path.Combine(AppDataPaths.ProjectRoot!, "Тесты", "SpeechAssets_20261109", "installed", "en-technical-probe.zip"));
        using var sourceManifest = JsonDocument.Parse(original.GetEntry("manifest.json")!.Open());
        var facts = sourceManifest.RootElement.GetProperty("Items")[0];
        var take = new SpeechAssetTake { Id = Guid.NewGuid().ToString("N"), File = "audio/probe.wav", Selected = true,
            Text = session.Rows[0].Text, Models = facts.GetProperty("Models").Deserialize<OutputModelUse[]>()!,
            Voice = facts.GetProperty("Voice").Deserialize<VideoNarratorChoice>()!, Seconds = facts.GetProperty("Seconds").GetDouble() };
        var source = store.FileFor(session.Id, take.File); Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        original.Entries.Single(e => e.FullName.EndsWith(".wav")).ExtractToFile(source, true);
        take.Sha256 = SpeechAssetStore.Hash(source); session.Rows[0].Takes.Add(take); session.Rows[0].Confirm(); store.Save(session);
        foreach (var format in Enum.GetValues<MusicAudioFormat>())
        {
            session.ExportFormat = format; session.ExportBitrate = 64;
            var path = Path.Combine(root, format + ".zip");
            await store.ExportAsync(session, path, default);
            using var zip = ZipFile.OpenRead(path);
            Assert.HasCount(2, zip.Entries);
            var audio = zip.Entries.Single(e => e.FullName.StartsWith("audio/"));
            Assert.IsTrue(audio.FullName.EndsWith(MusicOutputSettings.Extension(format)));
            StringAssert.Contains(audio.FullName, "voice.00ea4a3ee4914c14");
            using var bytes = new MemoryStream(); audio.Open().CopyTo(bytes);
            using var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open());
            var item = manifest.RootElement.GetProperty("Items")[0];
            Assert.AreEqual(Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant(), item.GetProperty("Sha256").GetString());
            Assert.AreEqual(take.Sha256, item.GetProperty("SourceSha256").GetString());
            Assert.AreEqual(format.ToString(), manifest.RootElement.GetProperty("Format").GetString());
            var decodedFile = Path.Combine(root, "probe" + MusicOutputSettings.Extension(format));
            File.WriteAllBytes(decodedFile, bytes.ToArray());
            var info = await AudioConversionMetadata.ReadAsync(MusicAudioRuntime.Default, decodedFile, default);
            StringAssert.Contains(info.Tags["LOPATA_PROVENANCE"], "video-narrator-qwen3-17b");
            using var marker = JsonDocument.Parse(info.Tags["LOPATA_SPEECH_ASSET"]);
            Assert.AreEqual("voice.00ea4a3ee4914c14", marker.RootElement.GetProperty("ClipId").GetString());
            Assert.AreEqual(take.Id, marker.RootElement.GetProperty("TakeId").GetString());
            Assert.AreEqual("en", marker.RootElement.GetProperty("Language").GetString());
            Assert.AreEqual(session.Rows[0].SourceText, marker.RootElement.GetProperty("SourceText").GetString());
            Assert.AreEqual(take.Text, marker.RootElement.GetProperty("SynthesisText").GetString());
            Assert.IsTrue(info.Duration > 1);
        }
        Assert.AreEqual(take.Sha256, SpeechAssetStore.Hash(source));
        var existing = Path.Combine(root, "cancel.zip"); File.WriteAllText(existing, "keep old archive");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.ExportAsync(session, existing, cancelled.Token));
        Assert.AreEqual("keep old archive", File.ReadAllText(existing));
    }
}
