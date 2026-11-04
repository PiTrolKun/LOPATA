using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Services.VideoEditor;

namespace AIHub.Services.VideoUpscale;

internal static class UpscaleMediaTools
{
    internal static string N(double n) => n.ToString("0.#########", CultureInfo.InvariantCulture);
    internal static async Task<string> Hash(string path, CancellationToken token)
    { await using var stream = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(stream, token)); }
    internal static async Task<UpscaleMedia> Describe(string path, CancellationToken token)
    {
        using var doc = await UpscaleFfmpeg.ProbeAsync(path, token);
        var streams = doc.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.First(s => s.GetProperty("codec_type").GetString() == "video");
        var rate = video.GetProperty("avg_frame_rate").GetString()!; var numbers = rate.Split('/');
        var fps = double.Parse(numbers[0], CultureInfo.InvariantCulture) / double.Parse(numbers.ElementAtOrDefault(1) ?? "1", CultureInfo.InvariantCulture);
        // Audio padding or a second, longer audio stream must not create extra video frames.
        var duration = video.TryGetProperty("duration", out var videoDuration) && double.TryParse(videoDuration.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? seconds : video.TryGetProperty("tags", out var tags) && tags.TryGetProperty("DURATION", out var tagDuration) && TimeSpan.TryParse(tagDuration.GetString(), CultureInfo.InvariantCulture, out var span)
                ? span.TotalSeconds : double.Parse(doc.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        if (!double.IsFinite(duration) || duration <= 0 || !double.IsFinite(fps) || fps is <= 0 or > 1000) throw new InvalidDataException("VideoUpscale.InvalidSource");
        return new() { Width = video.GetProperty("width").GetInt32(), Height = video.GetProperty("height").GetInt32(), Duration = duration,
            Fps = fps, Rate = rate, Frames = (long)Math.Round(duration * fps), AudioStreams = streams.Count(s => s.GetProperty("codec_type").GetString() == "audio") };
    }
    internal static async Task<UpscalePart> Check(string path, long frames, int width, int height, CancellationToken token)
    {
        using var doc = JsonDocument.Parse(await UpscaleFfmpeg.RunAsync(true, ["-v", "error", "-count_frames", "-select_streams", "v:0", "-show_entries", "stream=width,height,nb_read_frames", "-of", "json", path], token));
        var v = doc.RootElement.GetProperty("streams")[0];
        if (v.GetProperty("width").GetInt32() != width || v.GetProperty("height").GetInt32() != height || long.Parse(v.GetProperty("nb_read_frames").GetString()!, CultureInfo.InvariantCulture) != frames)
            throw new InvalidDataException("VideoUpscale.OutputMismatch");
        return new(path, await Hash(path, token), frames, width, height);
    }
    internal static async Task<bool> Valid(UpscalePart part, CancellationToken token) => File.Exists(part.Path) && await Hash(part.Path, token) == part.Sha256;
    internal static string ScaleFilter(int width, int height, string algorithm = "lanczos") =>
        $"scale={width}:{height}:flags={algorithm}:force_original_aspect_ratio=decrease:force_divisible_by=2,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,setsar=1";
    internal static string[] Lossless => ["-an", "-c:v", "ffv1", "-level", "3", "-pix_fmt", "gbrp16le"];
    internal static Task<string> Filter(string input, string output, string filter, CancellationToken token) => UpscaleFfmpeg.RunAsync(false,
        ["-v", "error", "-y", "-i", input, "-vf", filter, .. Lossless, output], token);
    internal static Task<string> Cut(string input, string output, UpscaleMedia media, long start, long frames, CancellationToken token) => UpscaleFfmpeg.RunAsync(false,
        ["-v", "error", "-y", "-ss", N(start / media.Fps), "-copyts", "-start_at_zero", "-i", input, "-map", "0:v:0", "-frames:v", frames.ToString(System.Globalization.CultureInfo.InvariantCulture), "-vf",
            $"fps={media.Rate},trim=start={N(start / media.Fps)}:end={N((start + frames) / media.Fps)},setpts=PTS-STARTPTS,setsar=1", .. Lossless, output], token);
    internal static async Task MakeProxy(string input, string output, double start, double length, CancellationToken token)
    {
        await UpscaleFfmpeg.RunAsync(false, ["-v", "error", "-y", "-ss", N(start), "-i", input, "-t", N(length), "-map", "0:v:0", "-map", "0:a:0?",
            "-vf", "setsar=1", "-c:v", "libopenh264", "-b:v", "80M", "-pix_fmt", "yuv420p", "-c:a", "aac", "-movflags", "+faststart", output], token);
    }
}
