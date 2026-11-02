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

[TestClass, DoNotParallelize]
public sealed class SongClipSemanticInstalledTests
{
    private const string SourceId = "e03b409e8c0249ebbcca4fd8a0c9ce7d";
    private const double Seconds = 20;
    private sealed record Fixtures(int Schema, string SourceProjectHash, string SongHash, string ReferenceHash,
        string GlitchProject, string GlitchAttempt, string H3Project, string H3Attempt, string CroppedHash = "");

    [TestMethod, TestCategory("InstalledRuntime")]
    public async Task InstalledCoreAndExecutorProduceLyricGroundedFullSongSamples()
    {
        if (Environment.GetEnvironmentVariable("LOPATA_SONG_SEMANTIC_TEST") != "1") {
            Assert.Inconclusive("Explicit LOPATA_SONG_SEMANTIC_TEST=1 opt-in is required."); return;
        }
        var models = Environment.GetEnvironmentVariable("LOPATA_SONG_SEMANTIC_MODELS");
        if (string.IsNullOrWhiteSpace(models) || !Path.IsPathFullyQualified(models)) {
            Assert.Inconclusive("LOPATA_SONG_SEMANTIC_MODELS must identify installed model storage."); return;
        }
        var engine = Environment.GetEnvironmentVariable("LOPATA_SONG_SEMANTIC_ENGINE") ?? "both";
        Assert.IsTrue(engine is "core" or "glitchframe" or "h3" or "both", "Use core, h3 or both.");
        var planOnly = Environment.GetEnvironmentVariable("LOPATA_SONG_SEMANTIC_PLAN_ONLY") == "1";
        var typedReference = Environment.GetEnvironmentVariable("LOPATA_SONG_SEMANTIC_TYPED_REFERENCE") == "1";
        var sourceId = Environment.GetEnvironmentVariable("LOPATA_SONG_SEMANTIC_SOURCE_ID") ?? SourceId;
        Assert.IsTrue(Guid.TryParseExact(sourceId, "N", out _));
        var root = Path.GetFullPath(Environment.GetEnvironmentVariable("LOPATA_SONG_SEMANTIC_EVIDENCE")
            ?? @"H:\AI_HUB\_tmp\songclip-semantic-094");
        var sourceRoot = Environment.GetEnvironmentVariable("LOPATA_SONG_SEMANTIC_SOURCE_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AI_HUB", "SongClip", "Projects");
        var sourceStore = new SongClipStore(sourceRoot); var sourceProject = sourceStore.Load(sourceId);
        Assert.IsTrue(sourceProject.Song.Length > 0, "The selected source project has no song.");
        var sourceSong = sourceStore.Resolve(sourceId, sourceProject.Song);
        var sourceReference = sourceProject.Reference.Length == 0 ? "" : sourceStore.Resolve(sourceId, sourceProject.Reference);
        var sourceProjectFile = sourceStore.Resolve(sourceId, "project.json");
        Assert.IsFalse(root.Equals(sourceStore.Root, StringComparison.OrdinalIgnoreCase)
            || root.StartsWith(sourceStore.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Evidence must be outside the user's source-project storage.");
        Directory.CreateDirectory(root);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30)); var token = deadline.Token;
        var sourceProjectHash = await Hash(sourceProjectFile, token); var sourceSongHash = await Hash(sourceSong, token);
        var sourceReferenceHash = sourceReference.Length == 0 ? "" : await Hash(sourceReference, token);
        var manifest = Path.Combine(root, "fixtures.json");
        var fixture = File.Exists(manifest) ? JsonSerializer.Deserialize<Fixtures>(await File.ReadAllTextAsync(manifest, token))!
            : new Fixtures(1, sourceProjectHash, sourceSongHash, sourceReferenceHash, Id(), Id(), Id(), Id());
        Assert.IsNotNull(fixture); Assert.AreEqual(1, fixture.Schema);
        Assert.AreEqual(sourceProjectHash, fixture.SourceProjectHash); Assert.AreEqual(sourceSongHash, fixture.SongHash);
        Assert.AreEqual(sourceReferenceHash, fixture.ReferenceHash);
        foreach (var id in new[] { fixture.GlitchProject, fixture.GlitchAttempt, fixture.H3Project, fixture.H3Attempt })
            Assert.IsTrue(Guid.TryParseExact(id, "N", out _), "Invalid persistent fixture identity.");
        if (!File.Exists(manifest)) SongClipStore.Atomic(manifest, fixture);

        var previousGate = ComponentLicenseGate.ConfirmAsync;
        // Headless tests cannot display consent or silently accept missing terms.
        // Keep a host-provided gate; otherwise admit only already recorded receipts.
        if (previousGate is null) ComponentLicenseGate.ConfirmAsync = ExistingConsentOnly;
        var failures = new List<string>();
        try {
            await VideoRuntime.PrepareAsync(token);
            var crop = Path.Combine(root, "source-first-20s.wav");
            if (!File.Exists(crop)) {
                var staging = Path.Combine(root, "source-first-20s.partial.wav");
                await VideoRuntime.RunAsync(false, ["-nostdin", "-v", "error", "-y", "-i", sourceSong, "-map", "0:a:0",
                    "-t", Seconds.ToString(CultureInfo.InvariantCulture), "-vn", "-c:a", "pcm_s16le", "-ar", "44100", "-ac", "2", staging], token);
                token.ThrowIfCancellationRequested(); File.Move(staging, crop);
            }
            var cropHash = await Hash(crop, token);
            if (fixture.CroppedHash.Length > 0) Assert.AreEqual(fixture.CroppedHash, cropHash, "Preserved source crop changed.");
            else { fixture = fixture with { CroppedHash = cropHash }; SongClipStore.Atomic(manifest, fixture); }
            using (var probe = await VideoRuntime.ProbeAsync(crop, token)) Assert.AreEqual(Seconds, Duration(probe.RootElement), .02);

            var trials = new List<(string Model, string Project, string Attempt)>();
            if (engine is "core" or "glitchframe" or "both") trials.Add(("glitchframe", fixture.GlitchProject, fixture.GlitchAttempt));
            if (engine is "h3" or "both") trials.Add((H3MusicRunner.ModelId, fixture.H3Project, fixture.H3Attempt));
            var store = new SongClipStore(Path.Combine(root, "projects"));
            foreach (var trial in trials) {
                try {
                    if (!planOnly) {
                        var cards = trial.Model == "glitchframe" ? GlitchframeComponents.Cards(models) : H3MusicComponents.Cards(models);
                        foreach (var card in cards) foreach (var file in card.Files) {
                            var path = RuntimeFileAudit.Contained(card.InstallDirectory, file.RelativePath);
                            Assert.IsTrue(File.Exists(path) && new FileInfo(path).Length == file.SizeBytes,
                                "Installed-only test will not download missing component: " + card.ModelArtifactId + "/" + file.RelativePath);
                            // This is the sole H3 artifact whose runtime preparer may
                            // repair by downloading; verify it locally before entry.
                            if (card.ModelArtifactId == VideoH3RuntimeCatalog.Id)
                                Assert.AreEqual(file.Sha256.ToLowerInvariant(), await Hash(path, token), "Installed runtime library archive is corrupt; this test will not repair/download it.");
                        }
                    }
                    var settings = new SongClipSettings { Width = 640, Height = 360, Fps = 24, Steps = 8, Interval = 10,
                        Seed = 742, Prompt = "", Quality = 85, Motion = new() { Width = 320, Height = 320, Steps = 8, RawFrames = 362, ContextFrames = 39 } };
                    var ownedSong = await store.ImportAsync(trial.Project, crop, token);
                    var ownedReference = sourceReference.Length == 0 ? "" : await store.ImportAsync(trial.Project, sourceReference, token);
                    var snapshot = store.Resolve(trial.Project, "attempts/" + trial.Attempt + "/snapshot.json");
                    var trialName = File.Exists(snapshot) ? (JsonSerializer.Deserialize<SongClipAttempt>(await File.ReadAllTextAsync(snapshot, token))
                        ?? throw new InvalidDataException("Empty persistent attempt snapshot.")).Name
                        : sourceProject.Name + " · Semantic 20s · " + trial.Model;
                    var attempt = new SongClipAttempt(trial.Attempt, trial.Project, ownedSong, ownedReference,
                        trialName, sourceProject.Lyrics, TimingsBefore(sourceProject.Timings, Seconds), settings, models, 8)
                        { Model = trial.Model, PlanningVersion = 1,
                            References = typedReference ? ownedReference.Length == 0 ? [] : [new(ownedReference, "performer",
                                "Исполнитель на изображении поёт песню в кадре. События о котах происходят вокруг него. На изображении один персонаж в разных ракурсах.")] : null };
                    var projectFile = store.Resolve(trial.Project, "project.json");
                    var project = File.Exists(projectFile) ? store.Load(trial.Project) : new SongClipProject {
                        Id = trial.Project, Name = attempt.Name, Song = ownedSong, Reference = ownedReference,
                        Lyrics = attempt.Lyrics, Timings = attempt.Timings, Model = attempt.Model, Settings = settings, References = attempt.References
                    };
                    Assert.AreEqual(attempt.Song, project.Song); Assert.AreEqual(attempt.Reference, project.Reference);
                    Assert.AreEqual(attempt.Lyrics, project.Lyrics); Assert.AreEqual(attempt.Timings, project.Timings);
                    Assert.AreEqual(attempt.Model, project.Model); Assert.AreEqual(attempt.Settings, project.Settings);
                    project.PendingAttemptId = attempt.Id; store.Save(project); store.EnsureAttemptSnapshot(attempt);
                    var job = store.Resolve(trial.Project, "attempts/" + trial.Attempt);
                    var logFile = Path.Combine(job, "installed-semantic-progress.jsonl"); var logGate = new object(); var watch = Stopwatch.StartNew();
                    void Progress(double fraction, string message) {
                        var line = JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, seconds = watch.Elapsed.TotalSeconds, fraction, message });
                        lock (logGate) File.AppendAllText(logFile, line + Environment.NewLine);
                        Console.WriteLine($"[{trial.Model}] {watch.Elapsed:c} {fraction:P0} {message}");
                    }
                    SongClipStore.Atomic(Path.Combine(job, "installed-semantic-run.json"), new {
                        startedUtc = DateTimeOffset.UtcNow, sourceProject = sourceId, planOnly, typedReference, requestedPlanner = SongClipPlannerModel.ModelIdFor(trial.Model), attempt
                    });
                    if (planOnly) {
                        using var audit = ComponentAudit.CaptureOperation();
                        await ApplicationBackgroundOperations.RetireModelsAsync();
                        var prepared = await SongClipPlanning.EnsureAsync(attempt, store, job, Progress, token);
                        watch.Stop(); AssertPlan(prepared); AssertReferencePlan(attempt, prepared!);
                        SongClipStore.Atomic(Path.Combine(job, "installed-semantic-plan.json"), new {
                            completedUtc = DateTimeOffset.UtcNow, wallSeconds = watch.Elapsed.TotalSeconds,
                            requestedPlanner = SongClipPlannerModel.ModelIdFor(trial.Model), attempt, plan = prepared
                        });
                        Console.WriteLine($"[{trial.Model}] PLAN COMPLETE {job}; {watch.Elapsed.TotalSeconds:0.00} seconds; planner {string.Join(", ", prepared!.Assistants.Select(model => model.Name))}");
                        continue;
                    }
                    var result = trial.Model == "glitchframe" ? await GlitchframeRunner.RunAsync(attempt, store, Progress, token)
                        : await H3MusicRunner.RunAsync(attempt, store, Progress, token);
                    watch.Stop(); Assert.AreEqual(Seconds, result.Seconds, .1);
                    var plan = await SongClipPlanning.ReadCompletedAsync(attempt, store, job, token);
                    AssertPlan(plan); Assert.IsNotNull(plan); AssertReferencePlan(attempt, plan);
                    var output = store.Resolve(trial.Project, result.File);
                    using var final = await VideoRuntime.ProbeAsync(output, token); var document = final.RootElement;
                    foreach (var kind in new[] { "video", "audio" }) {
                        var stream = document.GetProperty("streams").EnumerateArray().Single(value => value.GetProperty("codec_type").GetString() == kind);
                        Assert.AreEqual(Seconds, Duration(stream, document), .1, kind + " must cover the complete cropped song.");
                    }
                    var tag = document.GetProperty("format").GetProperty("tags").EnumerateObject()
                        .Single(property => property.Name.Equals("LOPATA_VIDEO", StringComparison.OrdinalIgnoreCase)).Value.GetString()!;
                    var provenance = JsonSerializer.Deserialize<OutputProvenance>(tag)!;
                    foreach (var assistant in plan.Assistants)
                        Assert.IsTrue(provenance.Models.Any(model => model.Id == assistant.Id && model.Revision == assistant.Revision && model.Role == assistant.Role));
                    if (trial.Model == H3MusicRunner.ModelId) await AssertMotionLink(job, token);
                    if (!project.Results.Any(saved => saved.Id == result.Id)) project.Results.Add(result);
                    project.PendingAttemptId = ""; store.Save(project);
                    SongClipStore.Atomic(Path.Combine(job, "installed-semantic-result.json"), new {
                        completedUtc = DateTimeOffset.UtcNow, wallSeconds = watch.Elapsed.TotalSeconds, result, output,
                        requestedPlanner = SongClipPlannerModel.ModelIdFor(trial.Model), plan, provenance
                    });
                    Console.WriteLine($"[{trial.Model}] COMPLETE {output}; {watch.Elapsed.TotalSeconds:0.00} seconds; planner {string.Join(", ", plan.Assistants.Select(model => model.Name))}");
                }
                catch (Exception error) {
                    var text = trial.Model + ": " + error; failures.Add(text); Console.WriteLine(text);
                    SongClipStore.Atomic(Path.Combine(root, trial.Model + "-failure.json"), new { atUtc = DateTimeOffset.UtcNow, error = text });
                    if (error is OperationCanceledException) break;
                }
            }
            Assert.AreEqual(sourceProjectHash, await Hash(sourceProjectFile, token), "The user's source project was modified.");
            Assert.AreEqual(sourceSongHash, await Hash(sourceSong, token), "The user's song was modified.");
            if (sourceReference.Length > 0) Assert.AreEqual(sourceReferenceHash, await Hash(sourceReference, token), "The user's reference was modified.");
            if (failures.Count > 0) Assert.Fail(string.Join(Environment.NewLine, failures));
        }
        finally { ComponentLicenseGate.ConfirmAsync = previousGate; }
    }

    private static void AssertPlan(SongClipVisualPlan? plan)
    {
        Assert.IsNotNull(plan); Assert.HasCount(2, plan.Scenes); Assert.IsTrue(plan.Assistants.Length > 0);
        Assert.IsTrue(plan.Assistants.All(model => model.Role == "song-to-clip semantic planning" && model.Revision.Length > 0));
    }

    private static void AssertReferencePlan(SongClipAttempt attempt, SongClipVisualPlan plan)
    {
        if (attempt.References is not { Length: > 0 }) return;
        Assert.IsNotNull(plan.ReferenceRoles);
        Assert.HasCount(attempt.References.Length, plan.ReferenceRoles);
        Assert.AreEqual("performer", plan.ReferenceRoles[0].Kind);
        foreach (var scene in plan.Scenes) {
            var prompt = SongClipReferencePrompt.Compose(attempt, plan, scene.Index);
            if (attempt.Model != H3MusicRunner.ModelId) continue;
            foreach (var heading in new[] { "subject_definitions:", "summary:", "retention_analysis:",
                "detailed_description:", "overall_soundscape:", "non_diegetic_music:" })
                Assert.Contains(heading, prompt);
            Assert.Contains("<Picture 1>", prompt); Assert.Contains("<Subject 1>", prompt);
            Assert.Contains(plan.ReferenceRoles[0].Description, prompt);
        }
    }

    private static Task ExistingConsentOnly(IReadOnlyList<string> ids, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var service = new ComponentLicenseService(Path.Combine(AppContext.BaseDirectory, "Licenses"), Path.Combine(AppDataPaths.BaseDirectory, "Licenses", "receipts.json"));
        var receipts = service.ReadReceipts(); var catalog = service.Entries;
        foreach (var id in ComponentLicenseService.ExpandSelection(ids, catalog)) {
            var entry = catalog.SingleOrDefault(value => value.Id == id) ?? throw new InvalidOperationException("Unregistered component license: " + id);
            if (!receipts.Any(receipt => receipt.Id == entry.Id && receipt.Terms == entry.Terms))
                throw new InvalidOperationException("Existing component consent required; this test never accepts terms: " + id);
        }
        return Task.CompletedTask;
    }
    private static async Task AssertMotionLink(string job, CancellationToken token)
    {
        var files = Directory.GetFiles(Path.Combine(job, "motion-chunks"), "*.json").Order(StringComparer.Ordinal).ToArray(); Assert.HasCount(2, files);
        using var first = JsonDocument.Parse(await File.ReadAllTextAsync(files[0], token)); using var second = JsonDocument.Parse(await File.ReadAllTextAsync(files[1], token));
        Assert.AreEqual(first.RootElement.GetProperty("data").GetProperty("sha256").GetString(), second.RootElement.GetProperty("identity").GetProperty("previousHash").GetString());
        Assert.AreEqual(0, first.RootElement.GetProperty("identity").GetProperty("contextFrames").GetInt32());
        Assert.AreEqual(39, second.RootElement.GetProperty("identity").GetProperty("contextFrames").GetInt32());
        Assert.AreEqual(2, second.RootElement.GetProperty("identity").GetProperty("index").GetInt32());
        using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(job, "worker-result.json"), token));
        Assert.AreEqual(2, receipt.RootElement.GetProperty("completedChunks").GetInt32());
    }
    private static string TimingsBefore(string lrc, double seconds) => string.Join("\n", lrc.Split('\n').Select(line => {
        var matches = Regex.Matches(line, @"\[(\d+):(\d+(?:\.\d+)?)\]");
        var keep = matches.Where(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 60
            + double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) < seconds).Select(match => match.Value).ToArray();
        return keep.Length == 0 ? "" : string.Concat(keep) + Regex.Replace(line, @"\[[^\]]*\]", "").Trim();
    }).Where(line => line.Length > 0));
    private static string Id() => Guid.NewGuid().ToString("N");
    private static async Task<string> Hash(string path, CancellationToken token)
    { await using var input = File.OpenRead(path); return Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token)); }
    private static double Duration(JsonElement document) => double.Parse(document.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
    private static double Duration(JsonElement stream, JsonElement document) => stream.TryGetProperty("duration", out var duration)
        ? double.Parse(duration.GetString()!, CultureInfo.InvariantCulture) : Duration(document);
}
