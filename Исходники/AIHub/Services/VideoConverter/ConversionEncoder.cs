using System.Globalization;
using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services.VideoEditor;
using AIHub.Services.VideoUpscale;

namespace AIHub.Services.VideoConverter;

internal sealed record ConversionEncoder(string Name, ConversionExecutor Executor, string? FeedPixel = null)
{
    internal string Label => Name == "copy" ? "Перенос без перекодирования"
        : Executor == ConversionExecutor.Gpu ? $"GPU NVIDIA ({Name})" : $"CPU ({Name})";

    internal static async Task<ConversionEncoder> SelectAsync(ConversionPlan plan, string input, string sample, CancellationToken token)
    {
        if (plan.Copy) return new("copy", ConversionExecutor.Auto);
        List<string> rejected = [];
        foreach (var encoder in Candidates(plan).OrderBy(e => e.Executor != plan.Options.Executor))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                await UpscaleFfmpeg.RunAsync(false, [
                    "-v", "error", "-nostdin", "-xerror", "-n", "-i", input, "-map", $"0:{plan.Media.Video.Index}",
                    "-an", "-sn", "-map_metadata", "-1", "-map_chapters", "-1", "-frames:v", "2",
                    .. encoder.VideoArguments(plan, plan.Bitrate), sample
                ], token, operation: "VideoConverter.Probe");
                var media = await ConversionMedia.ProbeAsync(sample, token);
                CheckPicture(plan, media);
                return encoder;
            }
            catch (Exception error) when (error is IOException or InvalidDataException)
            { rejected.Add(encoder.Name + " — " + ShortError(error)); }
            finally { if (File.Exists(sample)) File.Delete(sample); }
        }
        var reasons = rejected.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, rejected);
        throw new ConversionCompatibilityException("Кодировщики не смогли записать пробный фрагмент в этом профиле." + reasons);
    }

    private static IEnumerable<ConversionEncoder> Candidates(ConversionPlan plan)
    {
        var pixel = plan.Media.Pixel;
        var gpuPixel = pixel == "yuv420p10le" ? "p010le" : null;
        var yuv420 = pixel is "yuv420p" or "yuv420p10le";
        switch (plan.Options.Codec)
        {
            case ConversionCodec.Av1:
                if (yuv420 && !plan.Lossless) yield return new("av1_nvenc", ConversionExecutor.Gpu, gpuPixel);
                if (yuv420 && !plan.Lossless) yield return new("libsvtav1", ConversionExecutor.Cpu);
                yield return new("libaom-av1", ConversionExecutor.Cpu);
                break;
            case ConversionCodec.Hevc:
                if (yuv420) yield return new("hevc_nvenc", ConversionExecutor.Gpu, gpuPixel);
                if (pixel == "yuv420p") yield return new("libkvazaar", ConversionExecutor.Cpu);
                break;
            case ConversionCodec.Avc:
                if (pixel == "yuv420p") yield return new("h264_nvenc", ConversionExecutor.Gpu);
                if (pixel == "yuv420p" && !plan.Lossless) yield return new("libopenh264", ConversionExecutor.Cpu);
                break;
            case ConversionCodec.Vp9:
                yield return new("libvpx-vp9", ConversionExecutor.Cpu);
                break;
            case ConversionCodec.Ffv1:
                yield return new("ffv1", ConversionExecutor.Cpu);
                break;
        }
    }

    private IEnumerable<string> VideoArguments(ConversionPlan plan, int? bitrate)
    {
        if (plan.Copy) return ["-c:v:0", "copy"];
        List<string> args = ["-c:v:0", Name, "-fps_mode:v:0", "passthrough"];
        if (plan.Fps == 0) args.AddRange(["-enc_time_base:v:0", "demux"]);
        List<string> filters = [];
        if (plan.Resize) filters.Add($"scale={plan.Width}:{plan.Height}:flags=lanczos");
        if (plan.Resize || plan.Media.Turned)
            filters.Add($"setsar={plan.Sar.Replace(':', '/')}:max=65535");
        if (plan.Fps > 0) filters.Add($"fps={plan.Fps}:round=near");
        if (FeedPixel is not null) filters.Add($"format={FeedPixel}");
        if (filters.Count > 0) args.AddRange(["-filter:v:0", string.Join(',', filters)]);
        args.AddRange(["-pix_fmt:v:0", (FeedPixel is null ? "+" : "") + (FeedPixel ?? plan.Media.Pixel)]);
        foreach (var (key, value) in plan.Media.Color)
        {
            var flag = key switch
            {
                "color_space" => "colorspace", "color_transfer" => "color_trc", "chroma_location" => "chroma_sample_location",
                _ => key
            };
            args.AddRange(["-" + flag + ":v:0", value]);
        }
        if (plan.Options.Codec == ConversionCodec.Av1
            && plan.Media.Pixel is "yuv420p" or "yuv420p10le" or "yuv420p12le"
            && plan.Media.Color.GetValueOrDefault("chroma_location") is "left" or "topleft")
        {
            var position = plan.Media.Color["chroma_location"] == "left" ? "vertical" : "colocated";
            args.AddRange(["-bsf:v:0", $"av1_metadata=chroma_sample_position={position}"]);
        }
        var rate = bitrate is int bits ? $"{bits}k" : null;
        var cq = plan.Options.Quality switch { 1 => "43", 2 => "36", 3 => "30", 4 => "24", _ => "18" };
        switch (Name)
        {
            case "av1_nvenc" or "hevc_nvenc" or "h264_nvenc":
                args.AddRange(["-preset", "p5"]);
                if (plan.Lossless) args.AddRange(["-tune", "lossless", "-rc", "constqp", "-qp", "0"]);
                else args.AddRange(rate is null ? ["-rc", "vbr", "-cq", cq, "-b:v", "0"] : ["-rc", "vbr", "-b:v", rate]);
                break;
            case "libsvtav1":
                args.AddRange(["-preset", "8"]);
                args.AddRange(rate is null ? ["-crf", cq] : ["-b:v", rate]);
                break;
            case "libaom-av1":
                args.AddRange(["-cpu-used", "6", "-row-mt", "1"]);
                if (plan.Lossless) args.AddRange(["-aom-params", "lossless=1", "-b:v", "0"]);
                else args.AddRange(rate is null ? ["-crf", cq, "-b:v", "0"] : ["-b:v", rate]);
                break;
            case "libkvazaar":
                args.AddRange(["-kvazaar-params", plan.Lossless ? "preset=fast,lossless=1" : $"preset=fast,qp={cq}"]);
                if (!plan.Lossless && rate is not null) args.AddRange(["-b:v", rate]);
                break;
            case "libopenh264":
                args.AddRange(["-rc_mode", "bitrate", "-b:v", rate ?? $"{Math.Clamp((int)(plan.Width * (double)plan.Height * plan.Media.Fps * (.025 + plan.Options.Quality * .018) / 1000), 96, 120_000)}k"]);
                break;
            case "libvpx-vp9":
                args.AddRange(["-deadline", "good", "-cpu-used", "4", "-row-mt", "1"]);
                if (plan.Lossless) args.AddRange(["-lossless", "1", "-b:v", "0"]);
                else args.AddRange(rate is null ? ["-crf", cq, "-b:v", "0"] : ["-b:v", rate]);
                break;
            case "ffv1":
                args.AddRange(["-level", "3", "-coder", "1", "-context", "1", "-slicecrc", "1"]);
                break;
        }
        return args;
    }

    internal async Task EncodeAsync(ConversionPlan plan, string input, string path, int? bitrate, long scratchLimit,
        Action<double> announce, CancellationToken token)
    {
        List<string> args = ["-v", "error", "-nostdin", "-xerror", "-n", "-progress", "pipe:1", "-nostats",
            "-i", input, "-map", $"0:{plan.Media.Video.Index}", "-map_metadata", "0", "-map_chapters", "0",
            .. VideoArguments(plan, bitrate)];
        var indexes = new Dictionary<string, int> { ["v"] = 1 };
        foreach (var track in plan.Tracks)
        {
            var type = track.Source.Type switch { "audio" => "a", "subtitle" => "s", "video" => "v", _ => "t" };
            var n = indexes.GetValueOrDefault(type);
            indexes[type] = n + 1;
            args.AddRange(["-map", $"0:{track.Source.Index}", $"-c:{type}:{n}", track.Encoder]);
            if (track.Bitrate > 0) args.AddRange([$"-b:{type}:{n}", $"{track.Bitrate}k"]);
            var disposition = track.Source.Disposition;
            if (type is "a" or "s" or "v" && disposition.Length > 0) args.AddRange([$"-disposition:{type}:{n}", disposition]);
            if (track.Source.Language is { Length: > 0 } language) args.AddRange([$"-metadata:s:{type}:{n}", $"language={language}"]);
            if (track.Source.Title is { Length: > 0 } title)
            {
                args.AddRange([$"-metadata:s:{type}:{n}", $"title={title}"]);
                if (plan.Options.Container == ConversionContainer.Mp4) args.AddRange([$"-metadata:s:{type}:{n}", $"handler_name={title}"]);
            }
        }
        var provenance = plan.Media.Provenance ?? new OutputProvenance { Scenario = "video-converter", ModelHistory = "unknown" };
        var receipt = JsonSerializer.Serialize(new
        {
            Program = "LOPATA", Version = ApplicationBuildVersion.Value, Utc = DateTimeOffset.UtcNow,
            SourceCodec = plan.Media.Video.Codec, Codec = plan.Options.VideoCodec, Encoder = Name,
            Container = plan.Options.Extension,
            FrameWidth = plan.Width, FrameHeight = plan.Height, FramesPerSecond = plan.Fps == 0 ? (double?)null : plan.Fps,
            BitrateKbps = bitrate, plan.Lossless, plan.Copy
        });
        var needsArtwork = plan.Options.Container == ConversionContainer.Mp4 && plan.Tracks.Any(track => track.Source.IsArtwork);
        if (needsArtwork)
        {
            var comment = VideoImportProvenance.ConversionComment(plan.Media.Comment, provenance.Json(), receipt);
            args.AddRange(["-metadata", "comment=" + comment]);
        }
        else
        {
            args.AddRange(["-metadata", "LOPATA_VIDEO=" + provenance.Json(), "-metadata", "LOPATA_CONVERSION=" + receipt]);
            if (plan.Media.Comment is { } comment) args.AddRange(["-metadata", "comment=" + comment]);
        }
        if (plan.Options.Container == ConversionContainer.Mp4)
        {
            args.AddRange(["-movflags", needsArtwork ? "+faststart" : "+faststart+use_metadata_tags"]);
            if (plan.Options.Codec == ConversionCodec.Hevc) args.AddRange(["-tag:v:0", "hvc1"]);
        }
        // This is a scratch-space guard. A shortened or incomplete video fails verification.
        args.AddRange(["-fs", scratchLimit.ToString(CultureInfo.InvariantCulture), path]);
        try
        {
            await UpscaleFfmpeg.RunAsync(false, args, token, line =>
            {
                const string marker = "out_time_us=";
                if (line.StartsWith(marker, StringComparison.Ordinal)
                    && long.TryParse(line.AsSpan(marker.Length), CultureInfo.InvariantCulture, out var microseconds))
                    announce(Math.Clamp(microseconds / 1_000_000d / plan.Media.Duration, 0, .99));
            }, "VideoConverter.FFmpeg");
        }
        catch (IOException error) when (error.Message.Contains("Could not find tag for codec", StringComparison.Ordinal)
            || error.Message.Contains("codec not currently supported in container", StringComparison.Ordinal)
            || error.Message.Contains("is not supported in container", StringComparison.Ordinal))
        {
            throw new ConversionCompatibilityException(ShortError(error));
        }
    }

    internal static async Task<ConversionMedia> VerifyAsync(ConversionPlan plan, string path, CancellationToken token)
    {
        var result = await ConversionMedia.ProbeAsync(path, token);
        CheckPicture(plan, result);
        var tolerance = Math.Max(.15, 2 / plan.Media.Fps);
        if (Math.Abs(result.Duration - plan.Media.Duration) > tolerance || result.Chapters != plan.Media.Chapters)
            throw new InvalidDataException("Проверка результата обнаружила изменение длительности или глав.");
        if (plan.Fps > 0 && Math.Abs(result.Fps - plan.Fps) > .6)
            throw new InvalidDataException("Частота кадров результата не совпадает с выбранной.");
        if (plan.Fps == 0 && Math.Abs(result.Fps - plan.Media.Fps) > Math.Max(.6, plan.Media.Fps * .04))
            throw new InvalidDataException("Частота кадров результата отличается от исходной.");
        var input = plan.Tracks.Select(t => t.Source).Prepend(plan.Media.Video).ToArray();
        var actual = result.Tracks.Where(t => t.Index != result.Video.Index).ToArray();
        foreach (var kind in new[] { "audio", "subtitle", "attachment", "video" })
        {
            var expected = plan.Tracks.Where(t => t.Source.Type == kind).ToArray();
            var written = actual.Where(t => t.Type == kind).ToArray();
            if (expected.Length != written.Length) throw new ConversionCompatibilityException("Проверка результата обнаружила пропавшую или лишнюю дорожку.");
            foreach (var (track, writtenTrack) in expected.Zip(written))
            {
                if (track.Codec != writtenTrack.Codec || track.Source.Channels != writtenTrack.Channels
                    || track.Source.IsArtwork != writtenTrack.IsArtwork
                    || track.Encoder == "copy" && track.Source.SampleRate != writtenTrack.SampleRate
                    || track.Source.Language is { Length: > 0 } language && language != "und" && (writtenTrack.Language ?? "") != language)
                    throw new ConversionCompatibilityException($"Параметры дорожки №{track.Source.Index + 1} изменились.");
                if (kind == "audio" && (Math.Abs((track.Source.Start - input[0].Start) - (writtenTrack.Start - result.Video.Start)) > tolerance
                    || track.Source.Duration is double before && writtenTrack.Duration is double after && Math.Abs(before - after) > tolerance))
                    throw new InvalidDataException($"Проверка синхронизации дорожки №{track.Source.Index + 1} не пройдена.");
            }
        }
        if (result.Provenance is null || result.ConversionTag is null
            || plan.Media.Provenance is { } old && result.Provenance.Json() != old.Json())
            throw new InvalidDataException("Не удалось прочитать сведения об исходной генерации и перекодировании из готового файла.");
        return result;
    }

    internal static Task VerifyDecodingAsync(string path, CancellationToken token) =>
        UpscaleFfmpeg.RunAsync(false, ["-v", "error", "-nostdin", "-xerror", "-err_detect", "explode", "-i", path,
            "-map", "0:v:0", "-map", "0:a?", "-fps_mode:v:0", "passthrough", "-enc_time_base:v:0", "demux",
            "-c:v", "rawvideo", "-c:a", "pcm_s16le", "-f", "null", "NUL"], token, operation: "VideoConverter.FFmpeg");

    private static void CheckPicture(ConversionPlan plan, ConversionMedia result)
    {
        if (result.Video.Codec != plan.Options.VideoCodec || result.OrientedWidth != plan.Width || result.OrientedHeight != plan.Height
            || Math.Abs(ConversionMedia.Ratio(result.OrientedSar) - ConversionMedia.Ratio(plan.Sar)) > .001
            || (plan.Copy && plan.Media.Rotation != result.Rotation) || (!plan.Copy && result.Rotation != 0)
            || plan.Media.Pixel != result.Pixel)
            throw new ConversionCompatibilityException("Кодек, поворот, разрешение или формат цвета результата отличаются от плана.");
        var changedColor = plan.Media.Color.Keys.Where(key => result.Color.GetValueOrDefault(key) != plan.Media.Color[key]).ToArray();
        if (changedColor.Length > 0)
            throw new ConversionCompatibilityException("Параметры цвета отличаются от исходника (" + string.Join(", ", changedColor) + ").");
        if (plan.Media.DynamicHdr != result.DynamicHdr)
            throw new ConversionCompatibilityException("В результате отличаются сведения о динамическом HDR.");
    }

    internal static string ShortError(Exception error)
    {
        var lines = error.Message.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(Environment.NewLine, lines.Take(4).Select(line => line.Length > 320 ? line[..320] : line));
    }
}
