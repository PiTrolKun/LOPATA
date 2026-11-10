using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VoiceNotifications;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed partial class VoiceNotificationTests
{
    [TestMethod]
    public void QueueKeepsPriorityAndPromotesOneDuplicateWithinItsGroup()
    {
        var queue = new VoiceNotificationQueue();
        VoiceNotice Notice(string id, int group, string scope = "job") => new(id, "voice.same", group, scope, []);
        queue.Add(Notice("first", 2)); queue.Add(Notice("second", 2)); queue.Add(Notice("third", 2));
        queue.Add(Notice("error", 1)); queue.Add(Notice("log", 3)); queue.Add(Notice("third", 2));
        Assert.AreEqual(5, queue.Count);
        Assert.AreEqual("error", queue.Take(_ => true)!.EventId);
        Assert.AreEqual("first", queue.Take(_ => true)!.EventId);
        Assert.AreEqual("third", queue.Take(_ => true)!.EventId);
        Assert.AreEqual("second", queue.Take(_ => true)!.EventId);
        queue.Add(Notice("log", 3, "another-job")); Assert.AreEqual(2, queue.Count);
    }

    [TestMethod]
    public void TerminalStateRemovesOldStatusesAndLogsButKeepsOtherJobsAndErrors()
    {
        var queue = new VoiceNotificationQueue();
        queue.Add(new("loading", "voice.a", 2, "job", [], "model"));
        queue.Add(new("diagnostic", "voice.a", 3, "job", []));
        queue.Add(new("error", "voice.a", 1, "job", [], "model"));
        queue.Add(new("other", "voice.a", 2, "other", []));
        queue.Add(new("ready", "voice.a", 2, "job", [], "model", Terminal: true));
        CollectionAssert.AreEquivalent(new[] { "error", "other", "ready" }, queue.Pending.Select(item => item.EventId).ToArray());
    }

    [TestMethod]
    public void PlaybackFiltersByMaterialAndMicrophoneAndRespectsManualSpeechPause()
    {
        var media = new object(); var microphone = new object();
        try
        {
            VoicePlaybackHub.MediaState(media, true, "project:one");
            Assert.IsFalse(VoicePlaybackHub.Allows(["project:two"])); Assert.IsTrue(VoicePlaybackHub.Allows(["project:one"]));
            VoicePlaybackHub.MicrophoneState(microphone, true); Assert.IsFalse(VoicePlaybackHub.Allows(["project:one"]));
            VoicePlaybackHub.MicrophoneState(microphone, false);
            var paused = 0; var resumed = 0;
            using var speech = VoicePlaybackHub.RegisterSpeech(() => paused++, () => resumed++);
            using (VoicePlaybackHub.SuspendSpeech()) { Assert.AreEqual(1, paused); speech.SetActive(false); }
            Assert.AreEqual(0, resumed);
            speech.SetActive(true);
            using (VoicePlaybackHub.SuspendSpeech()) { Assert.AreEqual(2, paused); }
            Assert.AreEqual(1, resumed);
        }
        finally { VoicePlaybackHub.MediaState(media, false); VoicePlaybackHub.MicrophoneState(microphone, false); }
    }

    [TestMethod]
    public void ExistingSettingsEnableAllGroupsAndClampOnlyNotificationParameters()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{}")!;
        Assert.IsTrue(Enumerable.Range(1, 3).All(settings.VoiceNotifications.Allows));
        settings.VoiceNotifications.Speed = 10; Assert.AreEqual(1.25, settings.VoiceNotifications.Speed);
        settings.VoiceNotifications.Speed = .1; Assert.AreEqual(.75, settings.VoiceNotifications.Speed);
        settings.VoiceNotifications.Speed = double.NaN; Assert.AreEqual(1, settings.VoiceNotifications.Speed);
        settings.VoiceNotifications.Enabled = false;
        Assert.IsFalse(settings.VoiceNotifications.Allows(1)); Assert.IsTrue(settings.CoreVoice.Enabled);
    }

    [TestMethod]
    public void CatalogRecognizesRepresentativeErrorsStatusesDetailsAndEnglishTemplates()
    {
        var store = new VoiceNotificationStore();
        var data = store.Catalog(); var ru = new VoiceNotificationCatalog(data, "ru"); var en = new VoiceNotificationCatalog(data, "en");
        Assert.AreEqual(1, ru.Find("RagBooks.State.Failed")!.Group);
        Assert.AreEqual(2, en.Find("Tray.ResultReady")!.Group);
        Assert.AreEqual(3, ru.Find("model.cache_reused")!.Group);
        var translated = new VoiceNotificationCatalog(new(1, [new("voice.test", new() { ["en"] = "Saved" })],
            [new("localization:save", "voice.test", 2)], [new("localization:save", "en", "Saved file: {0}")]), "en");
        Assert.AreEqual("voice.test", translated.Match("Saved file: document.docx")!.ClipId);
        translated.AddTranslation(new Dictionary<string, string> { ["save"] = "Fichier enregistré" });
        Assert.AreEqual("voice.test", translated.Match("Fichier enregistré")!.ClipId);
    }

    [TestMethod, DataRow(true), DataRow(false)]
    public async Task ImportUsesSharedAuditAndRejectsBrokenReplacementWithoutLosingActivePack(bool auditEnabled)
    {
        using var fixture = new Fixture(); using var audit = ComponentAudit.UseTestStore(Path.Combine(fixture.Root, "audit"), auditEnabled);
        var plan = VoicePackageImporter.Inspect(fixture.Archive);
        await fixture.Store.ImportAsync(plan, CancellationToken.None);
        var before = await File.ReadAllTextAsync(Path.Combine(fixture.Localizations, "VoiceNotifications", "en", "active.json"));
        var package = await fixture.Store.LoadAsync("en", CancellationToken.None); Assert.AreEqual(3, package!.Items.Count);
        var broken = fixture.CreateArchive("broken.zip", corruptHash: true);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Store.ImportAsync(VoicePackageImporter.Inspect(broken), CancellationToken.None));
        Assert.AreEqual(before, await File.ReadAllTextAsync(Path.Combine(fixture.Localizations, "VoiceNotifications", "en", "active.json")));
        await File.AppendAllTextAsync(package.Resolve(package.Items["voice.a"]), "changed");
        if (auditEnabled) await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Store.LoadAsync("en", CancellationToken.None));
        else Assert.IsNotNull(await fixture.Store.LoadAsync("en", CancellationToken.None));
    }

    [TestMethod]
    public void ZipTraversalIsRejectedBeforeInstallingAnything()
    {
        using var fixture = new Fixture(); var path = Path.Combine(fixture.Root, "escape.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) { using var output = new StreamWriter(zip.CreateEntry("../escape.wav").Open()); output.Write("test"); }
        Assert.ThrowsExactly<InvalidDataException>(() => VoicePackageImporter.Inspect(path));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "escape.wav")));
    }

    [TestMethod]
    public void PlaybackLeadInKeepsEveryOriginalPcmByteAndPreservesTheSource()
    {
        using var fixture = new Fixture(); var source = Path.Combine(fixture.Root, "original.wav"); var target = Path.Combine(fixture.Root, "playback.wav");
        var samples = Enumerable.Range(0, 3200).Select(index => (byte)(index % 200 + 1)).ToArray();
        using (var output = new NAudio.Wave.WaveFileWriter(source, new NAudio.Wave.WaveFormat(16000, 16, 1))) output.Write(samples, 0, samples.Length);
        var originalHash = SHA256.HashData(File.ReadAllBytes(source));
        VoiceNotificationAudio.AddLeadIn(source, target);
        using var reader = new NAudio.Wave.WaveFileReader(target); var pcm = new byte[reader.Length];
        Assert.AreEqual(pcm.Length, reader.Read(pcm, 0, pcm.Length));
        var lead = 16000 * 2 * VoiceNotificationAudio.LeadInMilliseconds / 1000;
        Assert.IsTrue(pcm.Take(lead).All(value => value == 0)); CollectionAssert.AreEqual(samples, pcm.Skip(lead).ToArray());
        CollectionAssert.AreEqual(originalHash, SHA256.HashData(File.ReadAllBytes(source)));
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "LOPATA-VoiceTests-" + Guid.NewGuid().ToString("N"));
        internal string Localizations => Path.Combine(Root, "Localization");
        internal string Archive { get; }
        internal VoiceNotificationStore Store { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(Root); var builtIn = Path.Combine(Root, "BuiltIn"); Directory.CreateDirectory(builtIn);
            var clips = new[] { new VoiceClipText("voice.a", new() { ["en"] = "Status" }), new("voice.b", new() { ["en"] = "Detail" }), new("voice.error", new() { ["en"] = "Error" }) };
            var events = new[] { new VoiceEventBinding("a", "voice.a", 2), new("b", "voice.b", 3), new("error", "voice.error", 1) };
            File.WriteAllText(Path.Combine(builtIn, "catalog.json"), JsonSerializer.Serialize(new VoiceCatalogData(1, clips, events, [])));
            Archive = CreateArchive("source.zip");
            File.Copy(Archive, Path.Combine(builtIn, "en.zip"));
            File.WriteAllText(Path.Combine(builtIn, "bundles.json"), JsonSerializer.Serialize(new Dictionary<string, string> { ["en"] = VoicePackageImporter.Inspect(Archive).Revision }));
            Store = new(builtIn, Localizations, Path.Combine(Root, "Cache"));
        }
        internal string CreateArchive(string name, bool corruptHash = false)
        {
            var path = Path.Combine(Root, name); var wave = Wave(); var hash = Convert.ToHexString(SHA256.HashData(wave));
            var items = new[] { new VoiceAudioItem("voice.a", "audio/a.wav", corruptHash ? new string('0', 64) : hash, "en", "Status", "Status", .1),
                new("voice.b", "audio/b.wav", hash, "en", "Detail", "Detail", .1), new("voice.error", "audio/error.wav", hash, "en", "Error", "Error", .1) };
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var item in items) { using var output = zip.CreateEntry(item.Name).Open(); output.Write(wave); }
            using var text = new StreamWriter(zip.CreateEntry("manifest.json").Open()); text.Write(JsonSerializer.Serialize(new VoiceAudioManifest(2, items)));
            return path;
        }
        private static byte[] Wave()
        {
            using var output = new MemoryStream(); using var writer = new BinaryWriter(output);
            writer.Write("RIFF"u8); writer.Write(36 + 3200); writer.Write("WAVEfmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
            writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(3200); writer.Write(new byte[3200]);
            return output.ToArray();
        }
        public void Dispose()
        {
            var absolute = Path.GetFullPath(Root); var expected = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (absolute.StartsWith(expected, StringComparison.OrdinalIgnoreCase) && Directory.Exists(absolute)) Directory.Delete(absolute, true);
        }
    }
}
