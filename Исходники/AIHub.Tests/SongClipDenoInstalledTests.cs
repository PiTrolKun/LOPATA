using System.IO;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.SongClip;
using AIHub.Services.VideoEditor;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class SongClipDenoInstalledTests
{
    [TestMethod, TestCategory("InstalledRuntime")]
    public async Task DenoGeneratesAndReopensAnExactSizeMp4WithSong()
    {
        if (Environment.GetEnvironmentVariable("LOPATA_SONG_DENO_FIXTURE") is not { Length: > 0 } fixturePath)
        {
            Assert.Inconclusive("Set LOPATA_SONG_DENO_FIXTURE to a local test fixture JSON.");
            return;
        }
        var fixture = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(fixturePath))!;
        // Reuse verified installed components. New revisions still get their mandatory full audit.
        using var audit = ComponentAudit.UseTestStore(ComponentAudit.StoreDirectory, enabled: false);
        var store = new SongClipStore(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(fixturePath))!, "projects"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        var token = deadline.Token;
        var project = new SongClipProject { Name = "Deno RTX test", Model = H3MusicRunner.ModelId,
            Lyrics = fixture.Lyrics, Timings = fixture.Timings,
            Settings = new() { Seed = 742, Motion = new() { Mode = SongClipDeno.Mode }, Deno = fixture.Size,
                Prompt = "A close shot of the singer performing to the supplied song, with natural mouth movements and the reference outfit." } };
        project.Song = await store.ImportAsync(project.Id, fixture.Song, token);
        project.Reference = await store.ImportAsync(project.Id, fixture.Reference, token);
        project.References = [new(project.Reference, "performer", "Исполнитель на референсе поёт песню. Сохранить внешность и одежду.")];
        var attempt = new SongClipAttempt(Guid.NewGuid().ToString("N"), project.Id, project.Song, project.Reference,
            project.Name, project.Lyrics, project.Timings, project.Settings, fixture.ModelsRoot, 8)
            { Model = project.Model, PlanningVersion = 1, References = project.References };
        project.PendingAttemptId = attempt.Id;
        store.Save(project);
        store.EnsureAttemptSnapshot(attempt);
        Console.WriteLine(store.Folder(project.Id));

        void Progress(double fraction, string message) => Console.WriteLine($"{fraction:P0} {message}");
        var result = await H3MusicRunner.RunAsync(attempt, store, Progress, token);
        Assert.AreEqual(SongClipDeno.Mode, result.Mode);
        var output = store.Resolve(project.Id, result.File);
        using var probe = await VideoRuntime.ProbeAsync(output, token);
        var tracks = probe.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video = tracks.Single(stream => stream.GetProperty("codec_type").GetString() == "video");
        Assert.AreEqual(fixture.Size.Width, video.GetProperty("width").GetInt32());
        Assert.AreEqual(fixture.Size.Height, video.GetProperty("height").GetInt32());
        Assert.AreEqual("24/1", video.GetProperty("avg_frame_rate").GetString());
        Assert.IsTrue(tracks.Any(stream => stream.GetProperty("codec_type").GetString() == "audio"));
        if (fixture.Size.Accelerate)
        {
            var journal = store.Resolve(project.Id, "attempts/" + attempt.Id + "/progress.jsonl");
            var sparseCalls = File.ReadLines(journal)
                .Where(line => line.StartsWith("@motion ", StringComparison.Ordinal))
                .Sum(line => {
                    using var evt = JsonDocument.Parse(line[8..]);
                    return evt.RootElement.GetProperty("kind").GetString() == "deno-accelerated"
                        ? evt.RootElement.GetProperty("calls").GetInt32() : 0;
                });
            Assert.IsGreaterThan(0, sparseCalls, "The fixture must reach the accelerated GPU kernel.");
            Console.WriteLine($"Accelerated GPU calls: {sparseCalls}");
        }
        var hash = await H3MontageBinding.HashAsync(output, token);
        var replay = await H3MusicRunner.RunAsync(attempt, store, Progress, token);
        Assert.AreEqual(result.File, replay.File);
        Assert.AreEqual(hash, await H3MontageBinding.HashAsync(output, token));
        project.Results.Add(result);
        project.PendingAttemptId = "";
        store.Save(project);
        Assert.AreEqual(fixture.Size, store.Load(project.Id).Settings.Deno);
        Console.WriteLine(output);
    }

    private sealed record Fixture(string ModelsRoot, string Song, string Reference, string Lyrics, string Timings, SongClipDenoSettings Size);
}
