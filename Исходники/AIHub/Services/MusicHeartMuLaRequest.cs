using System.IO;
using System.Text.RegularExpressions;

namespace AIHub.Services;

public static class MusicHeartMuLaRequest
{
    public static Dictionary<string, object?> Build(MusicYueRequest request)
    {
        request.Validate(); var settings = request.EffectiveExpert;
        if (!MusicHeartMuLaCatalog.IsHeart(settings.Variation)) throw new InvalidDataException("HeartMuLa settings required.");
        var warnings = new List<string>();
        var instrumental = request.Wishes?.Instrumental == true;
        var lyrics = instrumental ? "" : request.Lyrics.Replace("\r\n", "\n");
        var tags = string.Join(',', request.Style.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase));
        if (instrumental) { tags = string.Join(',', new[] { tags, "instrumental,no vocals" }.Where(t => t.Length > 0)); warnings.Add("Instrumental is a tag wish, not a separate hard mode in the author API; lyrics are empty."); }
        if (request.Wishes?.Performers.Length > 1 || request.Wishes?.NoChoir == true || request.Wishes?.NoBacking == true || request.Wishes?.Selections.GetValueOrDefault("avoid")?.Length > 0)
            warnings.Add("Vocal roles, exclusions and restrictions are tag wishes; the author API has no hard role or negative-prompt control.");
        if (Regex.IsMatch(lyrics, "[А-Яа-яЁё]")) warnings.Add("Russian quality is not verified; text is sent unchanged to the author tokenizer.");
        return new() { ["lyrics"] = lyrics, ["original_lyrics"] = request.Lyrics, ["tags"] = tags, ["original_style"] = request.Style,
            ["seed"] = request.SoundSeed,
            ["max_audio_length_ms"] = checked((request.DurationAutomatic ? settings.Integer("max_duration") : request.DurationSeconds) * 1000),
            ["topk"] = settings.Integer("topk"), ["temperature"] = settings.Get("temperature"), ["cfg_scale"] = settings.Get("cfg_scale"),
            ["lazy_load"] = settings.Integer("lazy_load") == 1,
            ["mula_dtype"] = settings.Integer("mula_dtype"), ["codec_dtype"] = settings.Integer("codec_dtype"),
            ["codec_on_cpu"] = settings.Integer("codec_on_cpu") == 1, ["warnings"] = warnings,
            ["source_revision"] = MusicHeartMuLaCatalog.SourceRevision, ["model_revision"] = MusicHeartMuLaCatalog.ModelRevision,
            ["codec_revision"] = MusicHeartMuLaCatalog.CompanionRevision, ["config_revision"] = MusicHeartMuLaCatalog.ConfigRevision };
    }
}
