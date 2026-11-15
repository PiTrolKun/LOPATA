using System.IO;
using System.Text.Json;
using AIHub.Services.SongClip;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class SongClipWorkerRecoveryTests
{
    [TestMethod, TestCategory("InstalledRuntime")]
    [DataRow("OutOfMemoryError", "Injected allocation failure", true)]
    [DataRow("AcceleratorError", "CUDA error: unknown error\nCUDA kernel errors might be asynchronously reported", true)]
    [DataRow("AcceleratorError", "CUDA error: illegal memory access", false)]
    public async Task VideoWorkerRestartsWithOffloadOnlyForKnownRecoverableFailures(string type, string message, bool retry)
    {
        var python = Environment.GetEnvironmentVariable("LOPATA_MONTAGE_STARTUP_PYTHON");
        if (python is null)
        {
            Assert.Inconclusive("Explicit installed Python path required.");
            return;
        }

        using var files = new VideoPreparationTests.TestFiles();
        var request = files.File("request.json");
        var job = Path.GetDirectoryName(request)!;
        File.WriteAllText(request, JsonSerializer.Serialize(new { type, message }));
        var name = $"song-clip-recovery-{Guid.NewGuid():N}.py";
        var fixture = Path.Combine(AppContext.BaseDirectory, "Tools", name);
        await File.WriteAllTextAsync(fixture, """
            import json, os, sys
            from pathlib import Path

            request = Path(sys.argv[-1])
            pending = request.parent
            checkpoint = pending / 'first-shot.ready'
            with (pending / 'launches.jsonl').open('a', encoding='utf-8') as file:
                file.write(json.dumps({'offload': os.environ.get('LOPATA_H3_RETRY_OFFLOAD')}) + '\n')
            if not checkpoint.exists():
                checkpoint.write_bytes(b'first accepted shot')
                print('@motion {"kind":"chunk-start"}', flush=True)
                failure = json.loads(request.read_text(encoding='utf-8-sig'))
                print('@motion ' + json.dumps({'kind': 'error', **failure}), flush=True)
                sys.exit(1)

            assert os.environ.get('LOPATA_H3_RETRY_OFFLOAD') == '1'
            assert checkpoint.read_bytes() == b'first accepted shot'
            (pending / 'second-shot.ready').write_bytes(b'second accepted shot')
            print('@motion {"kind":"complete","fraction":1}', flush=True)
            """);
        try
        {
            var messages = new List<string>();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            Task Run() => H3MusicRunner.RunWorkerAsync(python, request, job, (_, status) => messages.Add(status),
                deadline.Token, worker: name, action: "--run");
            if (retry) await Run();
            else await Assert.ThrowsAsync<IOException>(Run);
            Assert.HasCount(retry ? 1 : 0, messages.Where(status => status == "SongClip.RenderRetrying").ToArray());
            Assert.AreEqual("first accepted shot", File.ReadAllText(Path.Combine(job, "first-shot.ready")));
            var second = Path.Combine(job, "second-shot.ready");
            if (retry) Assert.AreEqual("second accepted shot", File.ReadAllText(second));
            else Assert.IsFalse(File.Exists(second));
            string[] expectedLaunches = retry ? ["{\"offload\": \"0\"}", "{\"offload\": \"1\"}"] : ["{\"offload\": \"0\"}"];
            CollectionAssert.AreEqual(expectedLaunches,
                File.ReadAllLines(Path.Combine(job, "launches.jsonl")));
        }
        finally { File.Delete(fixture); }
    }
}
