using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.SongClip;
using AIHub.Services.VideoEditor;
using AIHub.Services.VideoPreparation;

namespace AIHub.Tests;

/// <summary>Explicit, resumable installed-runtime evidence; all user-owned files remain read-only.</summary>
[TestClass, DoNotParallelize]
public sealed class SongClipMontageInstalledTests
{
    private const string SourceId = "110a48e7559844fbb8d127dc0f14cf98";
    private const double CropStart = 19.84, CropSeconds = 10;
    private sealed record SourceFile(string Path, long Bytes, string Sha256);
    private sealed record Fixture(int Schema, string SourceId, string SourceRoot, SourceFile[] SourceFiles,
        double CropStart, double CropSeconds, string ProjectId, string AttemptId, string CroppedHash = "");
    private sealed record Artifact(string Path, long Bytes, string Sha256, DateTime LastWriteUtc);

    [TestMethod, TestCategory("InstalledRuntime")]
    public async Task AcquireOnlyMissingMontageComponentsWithPreviouslyRecordedConsent()
    {
        if (Environment.GetEnvironmentVariable("LOPATA_SONG_MONTAGE_ACQUIRE") != "1") {
            Assert.Inconclusive("Explicit LOPATA_SONG_MONTAGE_ACQUIRE=1 is required for managed acquisition."); return;
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var cards = H3MontageComponents.Cards(Models()); var previous = ComponentLicenseGate.ConfirmAsync;
        var evidence = EvidenceRoot(); Directory.CreateDirectory(evidence); var logGate = new object();
        var progress = new Progress<ManagedModelDownloadProgress>(value => {
            lock (logGate) File.AppendAllText(Path.Combine(evidence, "managed-acquisition.jsonl"),
                JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, value }) + Environment.NewLine);
            Console.WriteLine($"[montage acquire] {value.ModelArtifactId} {value.FileName}: {value.DownloadedBytes}/{value.TotalBytes}; {value.BytesPerSecond:0} B/s; {value.Stage}");
        });
        ComponentLicenseGate.ConfirmAsync = ExistingConsentOnly;
        try {
            // No new MIT or other terms are accepted by this headless helper.
            await ExistingConsentOnly(cards.Select(card => card.ModelArtifactId).ToArray(), deadline.Token);
            using var audit = ComponentAudit.CaptureOperation(); var store = new ManagedModelLibraryStore();
            using var acquisition = new ManagedModelAcquisitionService(store) { MaximumParallelConnections = 8 };
            foreach (var card in cards) {
                store.Upsert(card);
                if ((await acquisition.VerifyAsync(card.ModelArtifactId, progress, deadline.Token)).Status == ManagedModelStatuses.Installed) continue;
                Console.WriteLine("Acquiring missing managed component: " + card.ModelArtifactId);
                var installed = await acquisition.DownloadAsync(card.ModelArtifactId, progress, deadline.Token);
                Assert.AreEqual(ManagedModelStatuses.Installed, installed.Status, card.ModelArtifactId);
            }
        }
        finally { ComponentLicenseGate.ConfirmAsync = previous; }
    }

    [TestMethod, TestCategory("InstalledRuntime")]
    public async Task InstalledAnySongMontageUsesNativeBeatSlotsTypedReferenceAndImmutableCachedResults()
    {
        if (Environment.GetEnvironmentVariable("LOPATA_SONG_MONTAGE_TEST") != "1") {
            Assert.Inconclusive("Explicit LOPATA_SONG_MONTAGE_TEST=1 is required."); return;
        }
        var models = Models(); var root = EvidenceRoot();
        var sourceRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AI_HUB", "SongClip", "Projects");
        var sourceStore = new SongClipStore(sourceRoot); var sourceFolder = sourceStore.Folder(SourceId);
        Assert.IsTrue(Directory.Exists(sourceFolder), "The selected read-only source project must exist.");
        Assert.IsFalse(root.Equals(sourceStore.Root, StringComparison.OrdinalIgnoreCase)
            || root.StartsWith(sourceStore.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Evidence must be outside user project storage.");
        Directory.CreateDirectory(root);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(45)); var token = deadline.Token;
        var before = await HashSourceTree(sourceFolder, token);
        Assert.HasCount(107, before, "The agreed source snapshot changed; investigate instead of replacing the persistent fixture.");
        SongClipStore.Atomic(Path.Combine(root, "source-before.json"), before);
        var previousGate = ComponentLicenseGate.ConfirmAsync;
        // Always override even an interactive host gate: this opt-in test may only reuse existing consent.
        ComponentLicenseGate.ConfirmAsync = ExistingConsentOnly;
        try {
            var fixtureFile = Path.Combine(root, "fixtures.json");
            var fixture = File.Exists(fixtureFile)
                ? JsonSerializer.Deserialize<Fixture>(await File.ReadAllTextAsync(fixtureFile, token)) ?? throw new InvalidDataException("Empty montage fixture.")
                : new Fixture(1, SourceId, sourceStore.Root, before, CropStart, CropSeconds, Id(), Id());
            Assert.AreEqual(1, fixture.Schema); Assert.AreEqual(SourceId, fixture.SourceId);
            Assert.AreEqual(sourceStore.Root, fixture.SourceRoot); Assert.AreEqual(CropStart, fixture.CropStart); Assert.AreEqual(CropSeconds, fixture.CropSeconds);
            Assert.IsTrue(Guid.TryParseExact(fixture.ProjectId, "N", out _) && Guid.TryParseExact(fixture.AttemptId, "N", out _));
            CollectionAssert.AreEqual(fixture.SourceFiles, before, "Source content changed since the immutable fixture was created.");
            if (!File.Exists(fixtureFile)) SongClipStore.Atomic(fixtureFile, fixture);
            var source = sourceStore.Load(SourceId);
            var cards = H3MontageComponents.Cards(models);
            await ExistingConsentOnly(cards.Select(card => card.ModelArtifactId).ToArray(), token);
            await AssertInstalledOnly(cards, token);
            await VideoRuntime.PrepareAsync(token);
            var crop = Path.Combine(root, "song-19.84-to-29.84.wav");
            if (!File.Exists(crop)) {
                var staging = Path.Combine(root, "song-19.84-to-29.84.partial.wav");
                await VideoRuntime.RunAsync(false, ["-nostdin", "-v", "error", "-y", "-i", sourceStore.Resolve(SourceId, source.Song),
                    "-ss", CropStart.ToString(CultureInfo.InvariantCulture), "-t", CropSeconds.ToString(CultureInfo.InvariantCulture),
                    "-map", "0:a:0", "-vn", "-c:a", "pcm_s16le", "-ar", "44100", "-ac", "2", staging], token);
                token.ThrowIfCancellationRequested(); File.Move(staging, crop);
            }
            var croppedHash = await Hash(crop, token);
            if (fixture.CroppedHash.Length > 0) Assert.AreEqual(fixture.CroppedHash, croppedHash, "Preserved test audio changed.");
            else { fixture = fixture with { CroppedHash = croppedHash }; SongClipStore.Atomic(fixtureFile, fixture); }
            using (var probe = await VideoRuntime.ProbeAsync(crop, token)) Assert.AreEqual(CropSeconds, Duration(probe.RootElement), .02);
            var store = new SongClipStore(Path.Combine(root, "projects"));
            var song = await store.ImportAsync(fixture.ProjectId, crop, token);
            var references = new List<SongClipReference>();
            foreach (var reference in SongClipReferences.For(source)) {
                var file = await store.ImportAsync(fixture.ProjectId, sourceStore.Resolve(SourceId, reference.File), token);
                references.Add(new(file, reference.Kind.Length == 0 ? "performer" : reference.Kind,
                    reference.Description.Length == 0 ? "Исполнитель на изображении поёт песню в кадре, а события песни о котах происходят рядом. Изображение задаёт исполнителя, а не кота из текста песни." : reference.Description));
            }
            var reproduceDecode = Environment.GetEnvironmentVariable("LOPATA_SONG_MONTAGE_DECODE_REPRO") == "1";
            var width = reproduceDecode ? 608 : 320; var height = reproduceDecode ? 352 : 320;
            var settings = new SongClipSettings { Width = width, Height = height, Fps = 24, Quality = 85, Seed = 742,
                Prompt = source.Settings.Prompt, Motion = new() { Mode = SongClipModes.Montage },
                Montage = new() { Width = width, Height = height, Steps = reproduceDecode ? 12 : 8, ClipSeconds = 8 } };
            var attempt = new SongClipAttempt(fixture.AttemptId, fixture.ProjectId, song, references.FirstOrDefault()?.File ?? "",
                source.Name + " · Montage native 10s", source.Lyrics, CropTimings(source.Timings), settings, models, 8)
                { Model = H3MusicRunner.ModelId, PlanningVersion = 1, References = references.ToArray() };
            var project = new SongClipProject { Id = fixture.ProjectId, Name = attempt.Name, Song = attempt.Song,
                Reference = attempt.Reference, References = attempt.References, Lyrics = attempt.Lyrics, Timings = attempt.Timings,
                Settings = settings, Model = attempt.Model, PendingAttemptId = attempt.Id };
            var projectFile = store.Resolve(project.Id, "project.json");
            if (File.Exists(projectFile)) {
                var saved = store.Load(project.Id);
                Assert.AreEqual(attempt.Name, saved.Name); Assert.AreEqual(attempt.Song, saved.Song);
                Assert.AreEqual(attempt.Lyrics, saved.Lyrics); Assert.AreEqual(attempt.Timings, saved.Timings);
                Assert.AreEqual(settings, saved.Settings); Assert.IsTrue(SongClipReferences.Same(attempt.References, saved.References));
                project.Results = saved.Results;
            }
            store.Save(project); store.EnsureAttemptSnapshot(attempt);
            var job = store.Resolve(project.Id, "attempts/" + attempt.Id);
            var log = Path.Combine(job, "installed-montage-progress.jsonl"); var logGate = new object(); var watch = Stopwatch.StartNew();
            void Progress(double fraction, string message) {
                var line = JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, seconds = watch.Elapsed.TotalSeconds, fraction, message });
                lock (logGate) File.AppendAllText(log, line + Environment.NewLine);
                Console.WriteLine($"[montage] {watch.Elapsed:c} {fraction:P0} {message}");
            }
            SongClipStore.Atomic(Path.Combine(job, "installed-montage-run.json"), new {
                startedUtc = DateTimeOffset.UtcNow, sourceProject = SourceId, cropStart = CropStart, cropSeconds = CropSeconds,
                croppedHash, requestedPlanner = SongClipPlannerModel.ModelIdFor(attempt.Model), attempt
            });
            var result = await H3MusicRunner.RunAsync(attempt, store, Progress, token);
            var firstSeconds = watch.Elapsed.TotalSeconds;
            var plan = await VerifyResult(attempt, store, job, result, croppedHash, token);
            var artifacts = await StableArtifacts(job, token);
            var repeat = await H3MusicRunner.RunAsync(attempt, store, Progress, token);
            Assert.AreEqual(result.File, repeat.File); Assert.AreEqual(result.Seconds, repeat.Seconds, .001);
            CollectionAssert.AreEqual(artifacts, await StableArtifacts(job, token), "Cached replay rewrote accepted native shots/plan/receipts or MP4.");
            if (!project.Results.Any(saved => saved.Id == result.Id)) project.Results.Add(result);
            project.PendingAttemptId = ""; store.Save(project); watch.Stop();
            SongClipStore.Atomic(Path.Combine(job, "installed-montage-result.json"), new {
                completedUtc = DateTimeOffset.UtcNow, firstSeconds, cachedRepeatSeconds = watch.Elapsed.TotalSeconds - firstSeconds,
                result, output = store.Resolve(project.Id, result.File), actualShots = plan.Shots.Length, expectedShots = 2,
                nativeBeatAnalysisUnmodified = true, sourceProject = SourceId, sourceCount = before.Length,
                semanticReferencePromptSha256 = SongClipMontageProtocol.ReferencePromptHash(plan), plan
            });
            Console.WriteLine($"[montage] COMPLETE {store.Resolve(project.Id, result.File)}; {firstSeconds:0.00}s; actual native shots={plan.Shots.Length} (expected approximately 2; not rewritten).");
        }
        catch (Exception error) {
            SongClipStore.Atomic(Path.Combine(root, "installed-montage-failure.json"), new { utc = DateTimeOffset.UtcNow, error = error.ToString() });
            throw;
        }
        finally {
            ComponentLicenseGate.ConfirmAsync = previousGate;
            // Verify all 107 files even after cancellation, failed inference or missing consent.
            var after = await HashSourceTree(sourceFolder, CancellationToken.None);
            SongClipStore.Atomic(Path.Combine(root, "source-after.json"), after);
            CollectionAssert.AreEqual(before, after, "The user's source project or one of its original artifacts changed.");
        }
    }

    private static async Task<SongClipMontagePlan> VerifyResult(SongClipAttempt attempt, SongClipStore store, string job,
        SongClipResult result, string songHash, CancellationToken token)
    {
        Assert.AreEqual(CropSeconds, result.Seconds, .15);
        using var analysisRaw = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(job, "montage-analysis.json"), token));
        var analysis = JsonSerializer.Deserialize<SongClipMontageAnalysis>(analysisRaw.RootElement)!; analysis.Validate();
        Assert.AreEqual(songHash, analysisRaw.RootElement.GetProperty("songSha256").GetString());
        Assert.IsTrue(analysisRaw.RootElement.GetProperty("bpm").GetDouble() > 0);
        Assert.IsTrue(analysis.BeatTimes.Length > 0, "A song with audible rhythm should provide actual beat observations.");
        Assert.AreEqual(CropSeconds, analysis.DurationSeconds, .02);
        var plan = await SongClipMontagePlanning.ReadCompletedAsync(attempt, store, job, analysis, token);
        Assert.AreEqual(analysis.Slots.Length, plan.Shots.Length); Assert.IsTrue(plan.Shots.Length >= 2);
        Assert.IsTrue(plan.Assistants.All(model => model.Role == "song-to-clip semantic planning" && model.Revision.Length > 0));
        Assert.AreEqual(analysis.DurationSeconds, plan.Shots[^1].StartSeconds + plan.Shots[^1].DurationSeconds, .000001);
        if (attempt.References is { Length: > 0 }) {
            Assert.IsNotNull(plan.ReferenceRoles); Assert.HasCount(attempt.References.Length, plan.ReferenceRoles);
            Assert.IsTrue(plan.Shots.Any(shot => shot.PerformerVisible && shot.ReferenceIndices.Any(index => plan.ReferenceRoles[index - 1].Kind == "performer")));
        }
        using var worker = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(job, "worker-result.json"), token));
        var receipt = worker.RootElement;
        Assert.AreEqual("montage", receipt.GetProperty("generationMode").GetString());
        Assert.AreEqual(H3MontageComponents.SourceRevision, receipt.GetProperty("anySongSourceRevision").GetString());
        Assert.AreEqual(plan.Shots.Length, receipt.GetProperty("completedShots").GetInt32());
        await H3MontageReceipt.ValidateAsync(receipt, Path.Combine(job, "request.json"), attempt, store, plan, token);
        using var state = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(job, "montage-state.json"), token));
        Assert.AreEqual("montage", state.RootElement.GetProperty("identity").GetProperty("generationMode").GetString());
        Assert.AreEqual(songHash, state.RootElement.GetProperty("identity").GetProperty("songSha256").GetString());
        var previousHash = state.RootElement.GetProperty("inputHash").GetString();
        var files = Directory.GetFiles(Path.Combine(job, "montage-clips"), "*.json").Order(StringComparer.Ordinal).ToArray();
        Assert.HasCount(plan.Shots.Length, files);
        foreach (var file in files) {
            using var shot = JsonDocument.Parse(await File.ReadAllTextAsync(file, token)); var row = shot.RootElement;
            Assert.AreEqual(previousHash, row.GetProperty("identity").GetProperty("previousHash").GetString());
            var data = row.GetProperty("data"); var path = RuntimeFileAudit.Contained(job, data.GetProperty("file").GetString()!);
            previousHash = await Hash(path, token); Assert.AreEqual(previousHash, data.GetProperty("sha256").GetString());
            Assert.AreEqual(new FileInfo(path).Length, data.GetProperty("size").GetInt64());
        }
        Assert.AreEqual(previousHash, receipt.GetProperty("checkpointHash").GetString());
        using var probe = await VideoRuntime.ProbeAsync(store.Resolve(attempt.ProjectId, result.File), token);
        var document = probe.RootElement; Assert.AreEqual(CropSeconds, Duration(document), .15);
        var streams = document.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.Single(row => row.GetProperty("codec_type").GetString() == "video");
        var audio = streams.Single(row => row.GetProperty("codec_type").GetString() == "audio");
        Assert.AreEqual(attempt.Settings.Width, video.GetProperty("width").GetInt32());
        Assert.AreEqual(attempt.Settings.Height, video.GetProperty("height").GetInt32());
        foreach (var stream in new[] { video, audio })
            if (stream.TryGetProperty("duration", out var streamDuration)) Assert.AreEqual(CropSeconds, double.Parse(streamDuration.GetString()!, CultureInfo.InvariantCulture), .15);
        var tags = document.GetProperty("format").GetProperty("tags");
        var tag = tags.EnumerateObject().Single(item => item.Name.Equals("LOPATA_VIDEO", StringComparison.OrdinalIgnoreCase)).Value.GetString()!;
        var facts = JsonSerializer.Deserialize<OutputProvenance>(tag)!;
        Assert.AreEqual("song-to-clip/h3_music_context", facts.Scenario); Assert.Contains("montage", facts.ModelHistory);
        Assert.IsTrue(facts.GenerationSeconds > 0);
        Assert.IsTrue(facts.Models.Any(model => model.Id == H3MontageComponents.SourceId && model.Revision == H3MontageComponents.SourceRevision));
        foreach (var model in plan.Assistants) Assert.Contains(model, facts.Models);
        return plan;
    }

    private static async Task AssertInstalledOnly(IReadOnlyList<ManagedModelArtifactCard> cards, CancellationToken token)
    {
        foreach (var card in cards) foreach (var file in card.Files) {
            var path = RuntimeFileAudit.Contained(card.InstallDirectory, file.RelativePath);
            Assert.IsTrue(File.Exists(path) && new FileInfo(path).Length == file.SizeBytes,
                "Installed-only test will not download: " + card.ModelArtifactId + "/" + file.RelativePath);
            if (card.ModelArtifactId == VideoH3RuntimeCatalog.Id)
                Assert.AreEqual(file.Sha256.ToLowerInvariant(), await Hash(path, token), "Corrupt shared runtime archive; installed test will not repair/download it.");
        }
    }
    private static Task ExistingConsentOnly(IReadOnlyList<string> ids, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var service = new ComponentLicenseService(Path.Combine(AppContext.BaseDirectory, "Licenses"), Path.Combine(AppDataPaths.BaseDirectory, "Licenses", "receipts.json"));
        var receipts = service.ReadReceipts(); var entries = service.Entries;
        foreach (var id in ComponentLicenseService.ExpandSelection(ids, entries)) {
            var entry = entries.SingleOrDefault(item => item.Id == id) ?? throw new InvalidOperationException("Unregistered component license: " + id);
            if (!receipts.Any(receipt => receipt.Id == entry.Id && receipt.Terms == entry.Terms))
                throw new InvalidOperationException("Existing consent required; this test never accepts new terms, including MIT: " + id);
        }
        return Task.CompletedTask;
    }
    private static string Models()
    {
        var models = Environment.GetEnvironmentVariable("LOPATA_SONG_MONTAGE_MODELS");
        if (string.IsNullOrWhiteSpace(models) || !Path.IsPathFullyQualified(models))
            Assert.Inconclusive("LOPATA_SONG_MONTAGE_MODELS must identify installed model storage.");
        return Path.GetFullPath(models!);
    }
    private static string EvidenceRoot() => Path.GetFullPath(Environment.GetEnvironmentVariable("LOPATA_SONG_MONTAGE_EVIDENCE")
        ?? @"H:\AI_HUB\_tmp\songclip-montage-097\native");
    private static string CropTimings(string lrc)
    {
        var rows = new List<string> { "[re:LOPATA]" };
        foreach (var line in lrc.Replace("\r", "", StringComparison.Ordinal).Split('\n')) {
            var text = Regex.Replace(line, @"\[[^\]]*\]", "").Trim(); if (text.Length == 0) continue;
            foreach (Match match in Regex.Matches(line, @"\[(\d+):(\d+(?:[.:]\d+)?)\]")) {
                var seconds = 60 * int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)
                    + double.Parse(match.Groups[2].Value.Replace(':', '.'), CultureInfo.InvariantCulture);
                if (seconds < CropStart - .000001 || seconds >= CropStart + CropSeconds) continue;
                var shifted = seconds - CropStart; var minutes = (int)(shifted / 60);
                rows.Add("[" + minutes.ToString("00", CultureInfo.InvariantCulture) + ":" + (shifted - minutes * 60).ToString("00.00", CultureInfo.InvariantCulture) + "]" + text);
            }
        }
        return string.Join("\n", rows);
    }
    private static async Task<SourceFile[]> HashSourceTree(string root, CancellationToken token)
    {
        Lopata.Updates.SafeUpdatePath.RejectLinks(root); var rows = new List<SourceFile>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)) {
            Lopata.Updates.SafeUpdatePath.RejectLinks(file);
            rows.Add(new(Path.GetRelativePath(root, file).Replace('\\', '/'), new FileInfo(file).Length, await Hash(file, token)));
        }
        return rows.ToArray();
    }
    private static async Task<Artifact[]> StableArtifacts(string job, CancellationToken token)
    {
        var rows = new List<Artifact>();
        foreach (var file in Directory.EnumerateFiles(job, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)) {
            var name = Path.GetFileName(file);
            // Read-only replay may append diagnostics and reacquire the process
            // lock; only accepted media, plans, inputs and receipts are immutable.
            if (name is "elapsed.json" or "stderr.log" or "progress.jsonl" or "motion.lock"
                || name.StartsWith("installed-", StringComparison.Ordinal)) continue;
            rows.Add(new(Path.GetRelativePath(job, file), new FileInfo(file).Length, await Hash(file, token), File.GetLastWriteTimeUtc(file)));
        }
        return rows.ToArray();
    }
    private static string Id() => Guid.NewGuid().ToString("N");
    private static async Task<string> Hash(string file, CancellationToken token)
    { await using var stream = File.OpenRead(file); return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token)); }
    private static double Duration(JsonElement document) => double.Parse(document.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
}
