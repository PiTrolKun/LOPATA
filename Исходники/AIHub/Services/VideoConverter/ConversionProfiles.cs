using System.IO;

namespace AIHub.Services.VideoConverter;

internal sealed class ConversionCompatibilityException(string message) : IOException(message);

internal static class ConversionProfiles
{
    internal static IEnumerable<ConversionPlan> Candidates(ConversionOptions requested, ConversionMedia media)
    {
        requested.Validate();
        ConversionCompatibilityException? firstRejection = null;
        var found = false;
        foreach (var options in Options(requested))
        {
            ConversionPlan plan;
            try { plan = ConversionPlan.Create(options, media); }
            catch (ConversionCompatibilityException error) { firstRejection ??= error; continue; }

            found = true;
            if (options.Codec != requested.Codec || options.Container != requested.Container)
                plan = plan with { Changes = [$"Автоподбор · {Label(requested)} → {Label(options)}", .. plan.Changes] };
            yield return plan;
        }
        if (!found) throw firstRejection ?? new ConversionCompatibilityException("Для этого видео нет совместимого профиля.");
    }

    private static IEnumerable<ConversionOptions> Options(ConversionOptions requested)
    {
        ConversionCodec[] alternatives = requested.Codec switch
        {
            ConversionCodec.Avc => [ConversionCodec.Hevc, ConversionCodec.Av1, ConversionCodec.Vp9, ConversionCodec.Ffv1],
            ConversionCodec.Hevc => [ConversionCodec.Av1, ConversionCodec.Avc, ConversionCodec.Vp9, ConversionCodec.Ffv1],
            ConversionCodec.Vp9 => [ConversionCodec.Av1, ConversionCodec.Hevc, ConversionCodec.Avc, ConversionCodec.Ffv1],
            ConversionCodec.Ffv1 => [ConversionCodec.Av1, ConversionCodec.Hevc, ConversionCodec.Avc, ConversionCodec.Vp9],
            _ => [ConversionCodec.Hevc, ConversionCodec.Avc, ConversionCodec.Vp9, ConversionCodec.Ffv1]
        };
        foreach (var codec in alternatives.Prepend(requested.Codec))
        {
            if (codec == ConversionCodec.Ffv1 && requested.Mode is ConversionMode.Quality or ConversionMode.Bitrate) continue;
            foreach (var container in ConversionOptions.Containers(codec).OrderBy(c => c != requested.Container))
                yield return requested with
                {
                    Codec = codec,
                    Container = container,
                    Executor = codec == ConversionCodec.Ffv1 && requested.Executor == ConversionExecutor.Gpu
                        ? ConversionExecutor.Cpu : requested.Executor,
                    PictureMayChange = requested.PictureMayChange && requested.Codec != ConversionCodec.Ffv1
                };
        }
    }

    internal static string Label(ConversionOptions options)
    {
        var codec = options.Codec switch
        {
            ConversionCodec.Av1 => "AV1", ConversionCodec.Hevc => "H.265", ConversionCodec.Avc => "H.264",
            ConversionCodec.Vp9 => "VP9", _ => "FFV1"
        };
        return $"{codec} / {options.Extension[1..].ToUpperInvariant()}";
    }
}
