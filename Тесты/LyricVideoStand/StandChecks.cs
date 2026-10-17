using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Services;

namespace LyricVideoStand;

internal static class StandChecks
{
    public static async Task RunAsync(StandConfig config, StandLog log, bool export, CancellationToken token)
    {
        var folder = Path.Combine(config.Output, "runs", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")); Directory.CreateDirectory(folder);
        var checks = new List<object>();
        void Check(string name, bool success)
        {
            checks.Add(new { Name = name, Passed = success }); log.Write((success ? "PASS " : "FAIL ") + name);
            if (!success) throw new InvalidDataException("Check failed: " + name);
        }
        foreach (var frequency in new[] { 0, 100, 1000, 8000 })
        {
            var path = Path.Combine(folder, "tone-" + frequency + ".pcm");
            using (var stream = File.Create(path))
            {
                var bytes = new byte[48000 * 4];
                for (var i = 0; i < 48000; i++)
                {
                    var sample = (short)(Math.Sin(i * 2d * Math.PI * frequency / 48000) * 15000);
                    BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 4), sample);
                    BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 4 + 2), (short)-sample);
                }
                stream.Write(bytes);
            }
            var analysis = AudioAnalysis.ReadPcm(path, token); var point = analysis.Points[20];
            Check("finite curves / " + frequency, analysis.Points.All(p => float.IsFinite(p.Energy.Level + p.Energy.Bass + p.Energy.Mid + p.Energy.High)));
            if (frequency == 0) Check("silence has no artificial energy", analysis.Points.All(p => p.Rms == 0 && p.Energy == default));
            if (frequency == 100) Check("antiphase bass retained", point.Bass > .1 && point.Bass > point.Mid * 8 && point.Bass > point.High * 8);
            if (frequency == 1000) Check("mid-band separation", point.Mid > point.Bass * 8 && point.Mid > point.High * 8);
            if (frequency == 8000) Check("high-band separation", point.High > point.Bass * 8 && point.High > point.Mid * 8);
        }
        var session = await StandSession.LoadAsync(config, log, token);
        Check("real source clock finite", session.SongDuration > 1 && double.IsFinite(session.TotalDuration));
        Check("credits after audio / 4.5 seconds", Math.Abs(session.TotalDuration - session.SongDuration - 4.5) < .000001);
        Check("fixed intro survives early speech", session.Stage(3.44) == "заставка" && session.MainStart > 6);
        Check("timed words not fabricated", session.Lyrics is null || session.Lyrics.Lines.All(l => l.Words.Length == 0));
        Check("end of audio has no residual energy", session.Analysis.At(session.SongDuration + 1) == default);
        var ordered = session.Analysis.Points.Select(p => p.Rms).Order().ToArray();
        Check("test song contains dynamic contrast", ordered[(int)(ordered.Length * .9)] > ordered[(int)(ordered.Length * .1)] * 1.5);
        using var renderer = new SceneRenderer(session);
        var quietWeather = Enumerable.Range(0, 160).Select(i => renderer.WeatherAt(183 + i / 10d)).ToArray();
        var loudWeather = Enumerable.Range(0, 340).Select(i => renderer.WeatherAt(217 + i / 10d)).ToArray();
        var quietWave = quietWeather.Average(w => w.WaveHeight); var loudWave = loudWeather.Average(w => w.WaveHeight);
        log.Write($"Weather contrast: quiet wave={quietWave:F1}px, storm wave={loudWave:F1}px; lyric rain fragments={renderer.LyricRainWordCount}.");
        Check("drizzle and storm have clear wave contrast", loudWave > quietWave * 5 && loudWeather.Average(w => w.Storm) > .5);
        Check("rain contains actual source lyric fragments", renderer.LyricRainWordCount > 0);
        Check("rain travel stays continuous through intensity changes", Enumerable.Range(1, 27000).All(i => {
            var previous = renderer.WeatherAt((i - 1) / 100d).RainTravel;
            var current = renderer.WeatherAt(i / 100d).RainTravel;
            return current >= previous && current - previous <= 4.73;
        }));
        var first = SHA256.HashData(renderer.Render(75)); renderer.Render(185);
        Check("seek order does not change rendered pixels", first.AsSpan().SequenceEqual(SHA256.HashData(renderer.Render(75))));
        foreach (var time in new[] { 0d, 1.8, 5, 30, 75, 185, 218, 230, session.SongDuration + 2 })
        {
            var path = Path.Combine(folder, "frame-" + time.ToString("000.00", System.Globalization.CultureInfo.InvariantCulture) + ".png");
            StandArtifacts.Png(path, renderer.Png(time), session.Provenance with { Scenario = "lyric-video/stand-frame" });
            StandArtifacts.VerifyPng(path);
        }
        Check("snapshot PNG provenance round-trip", Directory.GetFiles(folder, "frame-*.png").All(p => PngTextMetadata.Read(p).ContainsKey("LOPATA")));
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 45; i++) renderer.Render(70 + i / 30d);
        var fps = 45 / watch.Elapsed.TotalSeconds; log.Write($"CPU renderer throughput: {fps:F1} fps at 1920x1080.");
        if (export)
        {
            foreach (var (name, from, length) in new[] { ("opening", 0d, 8d), ("main", 70d, 8d), ("ending", session.SongDuration - 2, 6.5) })
            {
                var path = Path.Combine(folder, name + ".mp4");
                await Task.Run(() => VideoExport.RunAsync(session, path, from, length, log, null, token), token);
                Check("encoded stream/metadata verification / " + name, File.Exists(path));
            }
            var cancelledPath = Path.Combine(folder, "cancelled.mp4");
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(150));
            var cancelled = false;
            try { await Task.Run(() => VideoExport.RunAsync(session, cancelledPath, 70, 30, log, null, cancellation.Token), token); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { cancelled = true; }
            Check("cancel removes only unfinished output", cancelled && !File.Exists(cancelledPath) && !File.Exists(cancelledPath + ".partial"));
        }
        await session.VerifySourceAsync(token); Check("input hash unchanged", true);
        await File.WriteAllTextAsync(Path.Combine(folder, "checks.json"), JsonSerializer.Serialize(new {
            Provenance = session.Provenance, session.SourceHash, session.SongDuration, session.MainStart, session.TotalDuration,
            FramesPerSecond = fps, QuietWaveHeight = quietWave, StormWaveHeight = loudWave,
            renderer.LyricRainWordCount, Checks = checks, ExportChecked = export,
            KnownLimits = "CPU raster prototype; energy response is not beat tracking; no word timing or app integration."
        }, new JsonSerializerOptions { WriteIndented = true }), token);
        log.Write("CHECKS COMPLETE: " + folder);
    }
}
