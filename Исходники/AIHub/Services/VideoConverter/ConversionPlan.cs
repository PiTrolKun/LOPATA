using System.Globalization;
using System.IO;

namespace AIHub.Services.VideoConverter;

internal sealed record ConversionTrackPlan(ConversionTrack Source, string Encoder, string Codec, int Bitrate = 0);

internal sealed record ConversionPlan(ConversionOptions Options, ConversionMedia Media, int Width, int Height, string Sar,
    bool Copy, bool Lossless, int Fps, bool Resize, long? Limit, long OtherBytes,
    int? Bitrate, long? Estimate, ConversionTrackPlan[] Tracks, string[] Changes)
{
    private static readonly string[] Mp4Audio = ["aac", "mp3", "ac3", "eac3", "alac", "opus"];
    private static readonly string[] WebAudio = ["opus", "vorbis"];
    private static readonly string[] TextSubtitle = ["mov_text", "subrip", "webvtt", "ass", "ssa"];

    internal static ConversionPlan Create(ConversionOptions options, ConversionMedia media)
    {
        options.Validate();
        var limit = options.Limit(media.Bytes);
        var reduction = limit.HasValue ? 1 - (double)limit.Value / media.Bytes : 0;
        var requestedHeight = options.ShortEdge;
        var resized = requestedHeight > 0;
        var (width, height) = Size(media, requestedHeight);
        var sar = resized ? "1:1" : media.OrientedSar;
        var requiresCopy = Math.Abs(media.Rotation) % 90 != 0 || media.DynamicHdr;
        var fps = options.FramesPerSecond;
        if (fps == 0 && options.MotionMayChange && reduction >= .30)
        {
            int lower = reduction >= .60 ? 24 : 30;
            if (media.Fps > lower + .5) fps = lower;
        }
        var keepTiming = fps > 0 && (fps > media.Fps + .01 || requiresCopy);
        if (keepTiming) fps = 0;
        var copy = media.Video.Codec == options.VideoCodec && !resized && fps == 0
            && (requiresCopy || !options.PictureMayChange || options.Codec == ConversionCodec.Ffv1)
            && (media.Rotation == 0 || options.Container == ConversionContainer.Mp4);
        var lossless = !options.PictureMayChange || options.Codec == ConversionCodec.Ffv1;
        if (!copy && requiresCopy)
            throw new ConversionCompatibilityException("В видео нестандартный поворот или динамический HDR. Для их сохранения требуется перенос исходной видеодорожки.");

        List<string> changes = [copy ? "Картинка без перекодирования" : lossless ? "Кодирование картинки без дополнительных потерь" : "Разрешено сжать картинку с потерями"];
        if (resized) changes.Add($"Кадр {width} × {height}");
        if (fps > 0) changes.Add($"Движение {fps} кадр/с");
        else if (keepTiming) changes.Add($"Сохранена частота исходника {media.Fps:0.###} кадр/с");
        if (media.Chapters > 0 && options.Container == ConversionContainer.WebM)
            throw new ConversionCompatibilityException("Контейнер WebM не сохранит главы видео.");

        List<ConversionTrackPlan> tracks = [];
        foreach (var track in media.Tracks.Where(t => t.Index != media.Video.Index))
        {
            var label = $"{(track.IsArtwork ? "Обложка" : TrackLabel(track.Type))} №{track.Index + 1} ({track.Codec})";
            var plan = Track(options, track, reduction, label, changes);
            if (plan is not null) tracks.Add(plan);
            else if (options.OmitIncompatibleTracks) changes.Add($"{label} будет исключена");
            else throw new ConversionCompatibilityException($"{label} несовместима с этим форматом.");
        }

        // Reserve a small per-stream/container budget and the best available audio estimate.
        var otherBytes = 8192 + (long)(media.Duration * (800 + 350 * tracks.Count))
            + tracks.Sum(t => TrackBytes(t, media.Duration));
        int? bitrate = null;
        if (!copy && !lossless)
        {
            if (limit is long cap)
                bitrate = (int)Math.Clamp(Math.Floor((cap - otherBytes) * 8d / media.Duration / 1000d * .96), 1, 200_000);
            if (options.Mode == ConversionMode.Bitrate) bitrate = options.BitrateKbps;
        }
        if (!copy && !lossless && limit.HasValue && (limit.Value <= otherBytes || bitrate is < 20))
        {
            if (options.Mode == ConversionMode.Size)
                throw new ConversionCompatibilityException("Заданный лимит слишком мал для видео и выбранных дорожек.");
            bitrate = 20;
        }
        long? estimate = bitrate is int kbps ? (long)(kbps * 125d * media.Duration) + otherBytes
            : copy && media.Video.Bitrate is long videoRate ? (long)(videoRate * media.Duration / 8d) + otherBytes
            : !lossless ? QualityEstimate(options, media, width, height, fps, otherBytes) : null;
        return new(options, media, copy ? media.OrientedWidth : width, copy ? media.OrientedHeight : height,
            sar, copy, lossless, fps, resized, limit, otherBytes, bitrate, estimate, tracks.ToArray(), changes.ToArray());
    }

    internal static (int Width, int Height) Size(ConversionMedia media, int shortEdge)
    {
        if (shortEdge == 0) return (media.OrientedWidth, media.OrientedHeight);
        var aspect = media.OrientedWidth * ConversionMedia.Ratio(media.OrientedSar) / media.OrientedHeight;
        if (!double.IsFinite(aspect) || aspect is < .05 or > 20)
            throw new InvalidDataException("У исходного видео нестандартные пропорции кадра.");
        static int Even(double value) => Math.Max(2, checked((int)Math.Round(value / 2d) * 2));
        return aspect >= 1 ? (Even(shortEdge * aspect), Even(shortEdge)) : (Even(shortEdge), Even(shortEdge / aspect));
    }

    private static ConversionTrackPlan? Track(ConversionOptions options, ConversionTrack track,
        double reduction, string label, List<string> changes)
    {
        if (track.IsArtwork)
            return options.Container == ConversionContainer.Mp4 && track.Codec is "mjpeg" or "png"
                ? new(track, "copy", track.Codec) : null;
        if (track.Type == "audio")
        {
            var compatible = options.Container switch
            {
                ConversionContainer.Mp4 => Mp4Audio.Contains(track.Codec),
                ConversionContainer.WebM => WebAudio.Contains(track.Codec),
                _ => track.Codec.Length > 0
            };
            var encoder = options.Container == ConversionContainer.WebM ? "libopus" : "aac";
            var targetCodec = encoder == "aac" ? "aac" : "opus";
            var target = track.Channels switch { 1 => 80, 2 => 128, _ => Math.Clamp(track.Channels * 56, 128, 448) };
            if (options.AudioMayChange && (!compatible || reduction >= .25 && (track.Bitrate ?? 0) > target * 1100L))
            {
                changes.Add($"{label} → {targetCodec.ToUpperInvariant()} {target} кбит/с");
                return new(track, encoder, targetCodec, target);
            }
            if (compatible) return new(track, "copy", track.Codec);
            return null;
        }
        if (track.Type != "subtitle")
        {
            // MKV can retain fonts and attached images needed by household subtitle and playback workflows.
            if (options.Container == ConversionContainer.Mkv && track.Type == "attachment") return new(track, "copy", track.Codec);
            return null;
        }
        if (options.Container == ConversionContainer.Mkv && track.Codec != "mov_text"
            || options.Container == ConversionContainer.Mp4 && track.Codec == "mov_text"
            || options.Container == ConversionContainer.WebM && track.Codec == "webvtt")
            return new(track, "copy", track.Codec);

        if (!TextSubtitle.Contains(track.Codec) || !options.OmitIncompatibleTracks) return null;
        var (name, codec) = options.Container switch
        {
            ConversionContainer.Mp4 => ("movtext", "mov_text"),
            ConversionContainer.WebM => ("webvtt", "webvtt"),
            _ => ("srt", "subrip")
        };
        changes.Add($"{label} → {codec} (оформление может измениться)");
        return new(track, name, codec);
    }

    private static long TrackBytes(ConversionTrackPlan track, double duration) => track.Source.Type switch
    {
        "audio" => (long)((track.Bitrate > 0 ? track.Bitrate * 1000L
            : track.Source.Bitrate ?? Math.Max(track.Source.Channels, 1) * 96_000L)
            * (track.Source.Duration ?? duration) / 8d),
        "subtitle" => 4096,
        "attachment" => 256 * 1024,
        "video" when track.Source.IsArtwork => 64 * 1024,
        _ => 0
    };

    private static long QualityEstimate(ConversionOptions options, ConversionMedia media, int width, int height, int fps, long otherBytes)
    {
        double video = media.Video.Bitrate is long rate ? rate * media.Duration / 8d : Math.Max(1, media.Bytes - otherBytes);
        var areaRatio = width * (double)height / (media.Width * (double)media.Height);
        return (long)(video * Math.Clamp(areaRatio, .02, 16) * (fps > 0 ? fps / media.Fps : 1) * (.25 + options.Quality * .15)) + otherBytes;
    }

    private static string TrackLabel(string type) => type switch
    {
        "audio" => "Звуковая дорожка", "subtitle" => "Дорожка субтитров", "video" => "Дополнительная видеодорожка",
        "attachment" => "Вложенная дорожка", _ => "Дорожка данных"
    };
    internal static string Number(double value) => value.ToString("0.#########", CultureInfo.InvariantCulture);
}
