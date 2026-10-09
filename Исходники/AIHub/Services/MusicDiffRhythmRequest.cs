using System.IO;
using System.Text.RegularExpressions;

namespace AIHub.Services;

public static class MusicDiffRhythmRequest
{
    public static Dictionary<string, object?> Build(MusicYueRequest request)
    {
        request.Validate(); var settings = request.EffectiveExpert;
        if (!MusicDiffRhythmCatalog.IsDiff(settings.Variation)) throw new InvalidDataException("DiffRhythm settings required.");
        var duration = request.DurationAutomatic ? MusicDiffRhythmCatalog.MaximumDuration : request.DurationSeconds;
        if (duration > MusicDiffRhythmCatalog.MaximumDuration) throw new InvalidDataException("DiffRhythm duration: 1–240 seconds; automatic = 240 second ceiling.");
        var warnings = new List<string>();
        if (request.Wishes?.Instrumental == true)
            throw new InvalidDataException("The pinned DiffRhythm 2 pipeline does not implement instrumental generation.");
        var lyrics = NormalizeSections(request.Lyrics, warnings);
        if (request.Wishes?.Performers.Length > 1 || request.Wishes?.NoChoir == true || request.Wishes?.NoBacking == true)
            warnings.Add("Vocal roles and restrictions are style wishes; this pipeline provides no separate hard control for them.");
        if (settings.Integer("experimental_ru") == 1 && Regex.IsMatch(lyrics, "[А-Яа-яЁё]"))
            warnings.Add("Experimental Russian eSpeak frontend; author model and decoder unchanged. Russian quality is unverified.");
        return new() { ["lyrics"] = lyrics, ["original_lyrics"] = request.Lyrics, ["style"] = request.Style,
            ["seed"] = request.SoundSeed, ["duration"] = duration, ["steps"] = settings.Integer("steps"),
            ["cfg"] = settings.Get("cfg"), ["solver"] = MusicDiffRhythmCatalog.Parameters.Single(p => p.Key == "solver").Choices![settings.Integer("solver")],
            ["fake_stereo"] = settings.Integer("fake_stereo") == 1, ["experimental_ru"] = settings.Integer("experimental_ru") == 1,
            ["warnings"] = warnings, ["source_revision"] = MusicDiffRhythmCatalog.SourceRevision,
            ["model_revision"] = MusicDiffRhythmCatalog.ModelRevision, ["companion_revision"] = MusicDiffRhythmCatalog.CompanionRevision };
    }
    internal static string NormalizeSections(string lyrics, List<string> warnings)
    {
        var known = new HashSet<string> { "start", "end", "intro", "verse", "chorus", "outro", "inst", "solo", "bridge", "hook", "break", "stop", "space" };
        return string.Join('\n', lyrics.Replace("\r\n", "\n").Split('\n').Select(line => {
            var text = line.Trim(); if (!text.StartsWith('[') || !text.EndsWith(']')) return line;
            var match = Regex.Match(text, @"^\[(?<kind>[A-Za-z]+)(?:\s*\d+)?(?:\s*:[^\]]*)?\]$");
            var kind = match.Groups["kind"].Value.ToLowerInvariant();
            if (!match.Success || !known.Contains(kind))
                throw new InvalidDataException("Unsupported DiffRhythm section: " + text);
            var normalized = "[" + kind + "]";
            if (text != normalized) warnings.Add("Section " + text + " sent as " + normalized + "; original lyrics retained.");
            return normalized;
        }));
    }
}
