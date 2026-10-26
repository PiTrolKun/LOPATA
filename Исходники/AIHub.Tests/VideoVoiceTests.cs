using System.IO;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VideoEditor;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoVoiceTests
{
    [TestMethod]
    public async Task ActualSpeechReceiptIsAtomicKeepsItsProfileAndReadsLegacyFormats()
    {
        using var f = new VideoMusicTests.Folder(); var path = Path.Combine(f.Path, "voice.json");
        OutputModelUse[] models = [new("qwen", "Ryan", "revision", "narration speech synthesis")];
        var profile = new VideoSpeechProfile("qwen3-17b", "ru", 100, new("Ryan", "calm"));
        await new VideoNarrationReceipt(models, profile).SaveAsync(path, default);
        Assert.AreEqual(profile, VideoNarrationReceipt.Read(path).Profile);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAsync<TaskCanceledException>(() => new VideoNarrationReceipt(models).SaveAsync(path, cancelled.Token));
        Assert.AreEqual(profile, VideoNarrationReceipt.Read(path).Profile);
        File.WriteAllText(path, JsonSerializer.Serialize(models)); Assert.IsNull(VideoNarrationReceipt.Read(path).Profile);
        File.WriteAllText(path, JsonSerializer.Serialize(models[0])); Assert.AreEqual("Ryan", VideoNarrationReceipt.Read(path).Models[0].Name);
        var author = new OutputModelUse("qwen-vision", "author", "rev", "video text author");
        CollectionAssert.AreEqual(new[] { author }, VideoNarrationReceipt.SourceModels(models.Append(author)).ToArray());
        OutputModelUse[] oldSpeech = [new("cuda", "previous GPU", "rev", "speech hardware runtime · cuda:0"),
            new("s3gen", "previous Kitten decoder", "rev", "Kitten GPU waveform decoder")];
        CollectionAssert.AreEqual(new[] { author }, VideoNarrationReceipt.SourceModels(models.Concat(oldSpeech).Append(author)).ToArray());
    }
    [TestMethod]
    public void ProfileChangesRetainMediaAndRoundTripWithUndoAndWholeRequest()
    {
        var n = new NarrationSegment { End = 2, Text = "Текст", AudioAssetId = "retained" };
        var untouched = new NarrationSegment { Start = 2, End = 4, Text = "Другой" };
        var p = new VideoProject { Narration = [n, untouched] }; var edit = new VideoEditing(p);
        var choice = new VideoNarratorChoice("qwen3-17b", "ru", 100, new("Ryan", "Спокойно", "cpu"));
        edit.Edit(x => VideoVoiceProfiles.Apply(x, choice, false, new HashSet<string> { n.Id }));
        Assert.IsTrue(n.Stale); Assert.AreEqual("retained", n.AudioAssetId); Assert.AreEqual("kokoro-sveta", untouched.Provider);
        Assert.AreEqual(choice.Id, edit.Project.DefaultVoice, "Whole narration must use the newly applied narrator too.");
        Assert.AreEqual(choice.Options, edit.Project.DefaultVoiceOptions);
        Assert.AreEqual(choice.Options, edit.Project.Clone().Narration[0].VoiceOptions);
        edit.Undo(); Assert.AreEqual("kokoro-sveta", edit.Project.Narration[0].Provider); Assert.AreEqual("retained", edit.Project.Narration[0].AudioAssetId);
        edit.Redo(); Assert.AreEqual(choice.Options, edit.Project.Narration[0].VoiceOptions);
        VideoVoiceProfiles.Apply(edit.Project, choice, true, new HashSet<string>());
        var request = new VideoWorkRequest(Guid.NewGuid().ToString("N"), "project", "speech", Whole: true, Voice: choice.Id, VoiceOptions: choice.Options);
        var restored = JsonSerializer.Deserialize<VideoWorkRequest>(JsonSerializer.Serialize(request))!;
        Assert.AreEqual(choice.Options, restored.VoiceOptions); Assert.AreEqual(choice.Options, edit.Project.Clone().DefaultVoiceOptions);
        var legacy = JsonSerializer.Deserialize<VideoProject>("{\"Narration\":[{\"End\":2}]}")!; legacy.Validate();
        Assert.AreEqual(new VideoVoiceOptions(), legacy.DefaultVoiceOptions); Assert.AreEqual("kokoro-sveta", legacy.Narration[0].Provider);
        Assert.AreEqual("stable", JsonSerializer.Deserialize<VideoVoiceOptions>("{\"Speaker\":\"Ryan\"}")!.Preset);
    }
    [TestMethod]
    public void NativeKittenUsesAnIsolatedPinnedCpuRuntimeAndItsOwnVoiceCatalog()
    {
        var model = VideoNarratorCatalog.Get("kitten2"); Assert.IsNull(model.Blocked); Assert.IsTrue(model.Speakers.Contains("Bruno"));
        VideoNarratorCatalog.Validate("kitten2", "ru", 100, new("Bruno", Preset: "expressive"));
        using var f = new VideoMusicTests.Folder(); var cards = VideoModelPreparation.Cards(f.Path, new("speech", ["kitten2"]));
        Assert.AreEqual(2, cards.Count); var runtime = cards.Single(c => c.Role == ManagedModelRoles.Tool);
        Assert.IsTrue(runtime.Files.Any(f => f.RelativePath.StartsWith("torch-2.13.0+cpu-", StringComparison.Ordinal)));
        Assert.IsFalse(runtime.InstallDirectory.Contains("PythonCpu", StringComparison.Ordinal));
        Assert.IsTrue(cards.Single(c => c.Role == ManagedModelRoles.Speech).Files.Any(f => f.RelativePath == "cpp/student_w4/decoder.pt"));
        var licenses = JsonSerializer.Deserialize<ComponentLicenseEntry[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Licenses", "catalog.json")))!;
        Assert.IsTrue(cards.All(c => licenses.Any(l => l.Id == c.ModelArtifactId)));
    }
    [TestMethod]
    public void LanguageAndCapabilityValidationPreventsSilentSubstitution()
    {
        VideoNarratorCatalog.Validate("qwen3-17b", "ru", 100, new("Ryan", "calm"));
        Assert.ThrowsExactly<InvalidDataException>(() => VideoNarratorCatalog.Validate("orpheus3b", "ru", 100, new("tara")));
        Assert.ThrowsExactly<InvalidDataException>(() => VideoNarratorCatalog.Validate("qwen3-06b", "en", 100, new("Ryan", "calm")));
        Assert.ThrowsExactly<InvalidDataException>(() => VideoNarratorCatalog.Validate("qwen3-17b", "ru", 80, new("Ryan")));
        Assert.ThrowsExactly<InvalidDataException>(() => VideoNarratorCatalog.Validate("supertonic3", "ru", 100, new("missing")));
        Assert.ThrowsExactly<InvalidDataException>(() => VideoNarratorCatalog.Validate("vosk010", "en", 100, new("0")));
        using var voskAdapter = new VideoNarratorRegistry().Create("vosk010");
        Assert.IsInstanceOfType<IConfiguredVideoNarrator>(voskAdapter);
        using var installedAdapter = new VideoNarratorRegistry().Create("supertonic3");
        Assert.IsInstanceOfType<IConfiguredVideoNarrator>(installedAdapter);
    }
    [TestMethod]
    public void PinnedBundlesShareOnlyTheCorrectRuntimeAndRegisterLicensesAndTags()
    {
        using var f = new VideoMusicTests.Folder();
        var cards = VideoModelPreparation.Cards(f.Path, new("speech", ["qwen3-17b", "qwen3-06b", "supertonic3", "kokoro-sveta"]));
        Assert.AreEqual(7, cards.Count); Assert.AreEqual(3, cards.Count(c => c.Role == ManagedModelRoles.Tool));
        var licenses = JsonSerializer.Deserialize<ComponentLicenseEntry[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Licenses", "catalog.json")))!;
        foreach (var card in cards)
        {
            Assert.IsTrue(licenses.Any(e => e.Id == card.ModelArtifactId));
            Assert.IsTrue(Path.IsPathFullyQualified(card.InstallDirectory));
            Assert.IsTrue(card.Files.Count > 0);
            foreach (var file in card.Files) { Assert.IsTrue(file.SizeBytes > 0); Assert.AreEqual(64, file.Sha256.Length); Assert.AreEqual("https", new Uri(file.SourceUrl).Scheme); }
            if (card.ModelArtifactId.StartsWith("video-narrator-runtime-", StringComparison.Ordinal))
                Assert.IsTrue(card.Files.Single(f => f.RelativePath.StartsWith("soundfile-", StringComparison.Ordinal)).RelativePath.EndsWith("-win_amd64.whl", StringComparison.Ordinal));
        }
        Assert.AreEqual(4, cards.Single(c => c.ModelArtifactId == "video-narrator-runtime-qwen").Files.Count(f => f.RelativePath.StartsWith("torchaudio-", StringComparison.Ordinal)));
        Assert.IsFalse(VideoNarratorCatalog.Cards("").Any(c => c.InstallDirectory.Length > 0));
        Assert.AreEqual(ScenarioNavigationCatalog.VideoEditor, ScenarioNavigationCatalog.GetTag("video_narrator_models").TargetId);
        var all = VideoNarratorCatalog.Cards(f.Path);
        Assert.AreEqual(16, all.Count); Assert.IsTrue(VideoNarratorCatalog.Models.All(m => m.Blocked is null));
        Assert.IsTrue(all.All(c => licenses.Any(e => e.Id == c.ModelArtifactId)));
        var installed = JsonSerializer.Deserialize<ManagedModelArtifactCard>(JsonSerializer.Serialize(all[0]))!;
        installed.Files[0].Sha256 = new string('0', 64);
        Assert.IsFalse(VideoNarratorCatalog.MatchesPin(installed, all[0]), "Same-revision stale pins must not execute.");
    }
    [TestMethod]
    public async Task PythonSourceArchivePreservesEmptyPackageFiles()
    {
        using var f = new VideoMusicTests.Folder(); var source = Path.Combine(f.Path, "antlr4-python3-runtime-4.9.3.tar.gz");
        using (var output = File.Create(source))
        using (var gzip = new GZipStream(output, CompressionMode.Compress))
        using (var tar = new TarWriter(gzip))
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "antlr4-python3-runtime-4.9.3/src/antlr4/__init__.py") { DataStream = new MemoryStream([]) });
        ManagedModelArtifactFile pin;
        await using (var input = File.OpenRead(source))
            pin = new() { RelativePath = Path.GetFileName(source), SizeBytes = input.Length, Sha256 = Convert.ToHexString(await SHA256.HashDataAsync(input)) };
        var root = Path.Combine(f.Path, "expanded");
        await VideoVoiceSourceArchive.ProcessAsync(pin, source, root, true, default);
        await VideoVoiceSourceArchive.ProcessAsync(pin, source, root, false, default);
        Assert.AreEqual(0L, new FileInfo(Path.Combine(root, "Lib", "site-packages", "antlr4", "__init__.py")).Length);
    }
    [TestMethod]
    public async Task PinnedVoiceZipRefusesTraversalAndFullAuditDetectsChangedPayload()
    {
        using var f = new VideoMusicTests.Folder(); var source = Path.Combine(f.Path, "voice.zip"); var root = Path.Combine(f.Path, "expanded"); Directory.CreateDirectory(root);
        using (var zip = ZipFile.Open(source, ZipArchiveMode.Create))
        { using var writer = new StreamWriter(zip.CreateEntry("source/package.py").Open()); writer.Write("original"); }
        async Task<ManagedModelArtifactFile> Pin() {
            await using var stream = File.OpenRead(source);
            return new() { RelativePath = "voice.zip", SizeBytes = stream.Length, Sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream)) };
        }
        var pin = await Pin(); await VideoVoiceZipArchive.ProcessAsync(pin, source, root, true, true, default);
        File.WriteAllText(Path.Combine(root, "package.py"), "modified");
        await Assert.ThrowsAsync<InvalidDataException>(() => VideoVoiceZipArchive.ProcessAsync(pin, source, root, false, true, default));
        var bad = Path.Combine(f.Path, "bad.zip");
        using (var zip = ZipFile.Open(bad, ZipArchiveMode.Create)) { using var writer = new StreamWriter(zip.CreateEntry("../escaped.py").Open()); writer.Write("invalid"); }
        await using var bytes = File.OpenRead(bad);
        var unsafePin = new ManagedModelArtifactFile { RelativePath = "bad.zip", SizeBytes = bytes.Length, Sha256 = Convert.ToHexString(await SHA256.HashDataAsync(bytes)) };
        await Assert.ThrowsAsync<InvalidDataException>(() => VideoVoiceZipArchive.ProcessAsync(unsafePin, bad, root, true, false, default));
        Assert.IsFalse(File.Exists(Path.Combine(f.Path, "escaped.py")));
    }
    [TestMethod]
    public async Task RuntimeAuditChecksFirstUseThenPresenceAndFullDetectsTampering()
    {
        using var f = new VideoMusicTests.Folder(); using var policy = ComponentAudit.UseTestStore(Path.Combine(f.Path, "audit"), false);
        var source = Path.Combine(f.Path, "example-1-py3-none-any.whl");
        using (var zip = ZipFile.Open(source, ZipArchiveMode.Create)) { using var writer = new StreamWriter(zip.CreateEntry("example/__init__.py").Open()); writer.Write("value = 1\n"); }
        var card = new ManagedModelArtifactCard { ModelArtifactId = "test-voice-runtime", Revision = "1", InstallDirectory = f.Path,
            Files = [new() { RelativePath = Path.GetFileName(source), SizeBytes = new FileInfo(source).Length, Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))), SourceUrl = "https://example.invalid/example.whl" }] };
        var directory = await VideoNarratorRuntime.PrepareAsync(card, default);
        var target = Path.Combine(directory, "example", "__init__.py"); await File.WriteAllTextAsync(target, "value = 2\n");
        await VideoNarratorRuntime.PrepareAsync(card, default); // OFF after a full success checks presence only.
        using (ComponentAudit.ForceFull()) await Assert.ThrowsAsync<InvalidDataException>(() => VideoNarratorRuntime.PrepareAsync(card, default));
        File.Delete(target);
        await Assert.ThrowsAsync<FileNotFoundException>(() => VideoNarratorRuntime.PrepareAsync(card, default));
        await VideoNarratorRuntime.PrepareAsync(card, default, reinstall: true);
        Assert.AreEqual("value = 1\n", File.ReadAllText(target));
        Assert.AreEqual(1, Directory.GetDirectories(f.Path, "Expanded.previous-*").Length);
    }
}
