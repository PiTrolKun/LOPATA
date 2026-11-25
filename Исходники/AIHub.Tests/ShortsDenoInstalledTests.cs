using System.IO;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.Shorts;
using AIHub.Services.SongClip;
using AIHub.Services.VideoEditor;
using AIHub.Services.VideoPreparation;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class ShortsDenoInstalledTests
{
    [TestMethod, TestCategory("InstalledRuntime")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DenoProducesVerticalShortWithAudio(bool fromSong)
    {
        var root = Environment.GetEnvironmentVariable("LOPATA_SHORTS_DENO_MODELS");
        var stand = Environment.GetEnvironmentVariable("LOPATA_SHORTS_DENO_STAND");
        var song = Environment.GetEnvironmentVariable("LOPATA_SHORTS_DENO_SONG");
        var reference = Environment.GetEnvironmentVariable("LOPATA_SHORTS_DENO_REFERENCE");
        if (root is null || stand is null || (fromSong && song is null))
        {
            Assert.Inconclusive("Opt-in Deno Shorts test requires an installed model root and a stand folder.");
            return;
        }
        using var audit = ComponentAudit.UseTestStore(ComponentAudit.StoreDirectory, enabled: false);
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        var token = stop.Token;
        var name = (fromSong ? "song" : "dialogue") + "-" + Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(stand);
        var output = Path.Combine(stand, name + ".mp4");
        var draft = fromSong
            ? new ShortsDraft(FromSong: true, Song: song!, ExcerptFrames: 120, OutputPath: output,
                References: reference is null ? null : [new(reference, "The solo male vocalist")])
            : new ShortsDraft(Prompt: "Vertical single-take close-up of a woman by a cabin window. Her face is visible. "
                + "In the first three seconds she clearly says aloud, in English, “The rain finally stopped. Let's go outside.” "
                + "She speaks once with natural mouth movement, then looks quietly out the window. We hear the last few drops tapping the glass and soft room ambience. No music.",
                OutputPath: output) { UseDeno = true, AccelerateDeno = true };
        var job = await ShortsJob.CreateAsync(draft, root, 8, token);
        var report = output + ".log";
        File.WriteAllText(report, $"Job {job.Id}{Environment.NewLine}");
        void Log(string line) { lock (report) File.AppendAllText(report, line + Environment.NewLine); }
        Assert.AreEqual(output, await ShortsRunner.RunAsync(job, Log, token));
        var saved = ShortsJob.Load(job.Id);
        Assert.IsTrue(ShortsGenerationProfile.FromJob(saved).Accelerate);
        if (fromSong && reference is not null)
        {
            var source = saved.Song!.References!.Single().File;
            Assert.AreEqual(Path.GetFullPath(reference), ShortsJob.Assets.Resolve(job.Id, source));
            Assert.IsTrue(File.Exists(reference));
        }
        else if (!fromSong)
        {
            Assert.IsTrue(saved.Description!.Run.Complete);
            Assert.IsTrue(saved.Description.Run.Takes.Skip(1).Any(take => take.Parent.Length > 0));
        }
        var log = File.ReadAllText(report);
        StringAssert.Contains(log, "deno-accelerated");
        Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(log, @"""calls""\s*:\s*[1-9]"),
            "The GPU acceleration path must run in at least one pass.");
        using var probe = await VideoRuntime.ProbeAsync(output, token);
        var tag = probe.RootElement.GetProperty("format").GetProperty("tags").GetProperty("LOPATA_VIDEO");
        var provenance = JsonSerializer.Deserialize<OutputProvenance>(tag.GetString()!);
        Assert.AreEqual("shorts/" + (fromSong ? "from-song" : "from-description"), provenance?.Scenario);
        Log(output);
    }
}
