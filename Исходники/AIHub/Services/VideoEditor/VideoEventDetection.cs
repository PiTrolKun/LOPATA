using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIHub.Services.VideoEditor;

internal readonly record struct VideoSceneChange(double At, double Score);
internal static partial class VideoEventDetection
{
    internal const string Revision = "visible-change-2";
    private const double SmallChange = .005, LargeChange = .12, BurstGap = .8, MinimumSpan = 1.25;
    internal static async Task<List<NarrationSegment>> DetectAsync(VideoAsset asset, string work, CancellationToken token)
    {
        Directory.CreateDirectory(work);
        var scores = new List<VideoSceneChange>(); var empty = Path.Combine(work, "events-empty.raw");
        if (File.Exists(empty)) File.Delete(empty);
        try {
            // select logs measured scene scores at debug level. Selecting no frames avoids
            // writing raw video; the bounded runtime reader consumes the log incrementally.
            await VideoRuntime.RunAsync(false, ["-hide_banner", "-nostdin", "-v", "debug", "-n", "-i", asset.Path, "-map", "0:" + asset.VideoStream,
                "-vf", "fps=5,scale=160:90,select='lt(scene,0)'", "-an", "-fps_mode", "vfr", "-pix_fmt", "gray", "-f", "rawvideo", empty], token, line => {
                var match = ScorePattern().Match(line);
                if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var at)
                    && double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var score)) scores.Add(new(at, score));
            });
        }
        finally { if (File.Exists(empty)) File.Delete(empty); }
        if (scores.Count == 0) throw new InvalidDataException("VideoEditor.EventsUnavailable");
        var segments = Segment(asset.Duration, scores);
        await File.WriteAllTextAsync(Path.Combine(work, "event-detection.json"), JsonSerializer.Serialize(new {
            Revision, SampleFps = 5, SmallChange, LargeChange, BurstGap, MinimumSpan,
            Measurements = scores.Count, Candidates = scores.Where(s => s.Score >= SmallChange).ToArray(),
            Boundaries = segments.Select(n => n.Start).Append(asset.Duration).ToArray()
        }, new JsonSerializerOptions { WriteIndented = true }), token);
        return segments;
    }
    internal static List<NarrationSegment> Segment(double duration, IEnumerable<VideoSceneChange> changes)
    {
        if (!double.IsFinite(duration) || duration <= 0) throw new InvalidDataException("VideoEditor.UnsupportedMedia");
        var boundaries = new List<double> { 0 }; var previousChange = double.NegativeInfinity;
        foreach (var change in changes.Where(s => double.IsFinite(s.At) && double.IsFinite(s.Score) && s.Score >= SmallChange).OrderBy(s => s.At))
        {
            // A menu animation or continuous scroll is one burst. A large scene cut
            // can still introduce a boundary inside otherwise continuous movement.
            var newBurst = change.At - previousChange >= BurstGap; previousChange = change.At;
            if ((newBurst || change.Score >= LargeChange) && change.At - boundaries[^1] >= MinimumSpan && duration - change.At >= .75) boundaries.Add(change.At);
        }
        boundaries.Add(duration);
        return boundaries.Zip(boundaries.Skip(1), (start, end) => new NarrationSegment { Start = start, End = end }).ToList();
    }
    [GeneratedRegex(@"\bt:([0-9.]+).*\bscene:([0-9.]+)")]
    private static partial Regex ScorePattern();
}
