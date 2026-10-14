using AIHub.Models;

namespace AIHub.Services;

/// <summary>Use song provenance first. A silent introduction must not override a clear Russian source.</summary>
public static class MusicLyricsSpeechLanguage
{
    public static string Resolve(MusicGenerationJob job)
    {
        var explicitLanguage = job.Expert.TextValues.GetValueOrDefault("vocal_language");
        if (explicitLanguage is not null and not ("auto" or "unknown") && explicitLanguage.Length > 0) return explicitLanguage;
        var choices = job.Wishes?.Selections.GetValueOrDefault("language") ?? [];
        if (choices.Length > 1) return "auto";
        if (choices.Length == 1 && Code(choices[0]) is { } selected) return selected;
        var performers = job.Wishes?.Performers.Select(performer => Code(performer.Language)).OfType<string>().Distinct().ToArray() ?? [];
        if (performers.Length > 1) return "auto";
        if (performers.Length == 1) return performers[0];
        var body = string.Join('\n', MusicTimedLyrics.Normalize(job.Lyrics).Split('\n')
            .Where(line => !(line.Trim().StartsWith('[') && line.Trim().EndsWith(']'))));
        var letters = body.Count(char.IsLetter);
        // Conservative script fallback for old Russian submissions without a language snapshot.
        // Other scripts and explicitly multilingual songs retain Whisper's automatic language detection.
        if (letters >= 12 && body.Count(c => c is >= 'А' and <= 'я' or 'Ё' or 'ё') >= letters * .85 &&
            !body.Any(c => "іїєґІЇЄҐўЎђЂљЉњЊћЋџЏјЈ".Contains(c))) return "ru";
        return "auto";
    }
    private static string? Code(string language) => language switch {
        "russian" => "ru", "english" => "en", "chinese" => "zh", "japanese" => "ja", "korean" => "ko",
        "french" => "fr", "german" => "de", "spanish" => "es", "italian" => "it", "portuguese" => "pt", _ => null
    };
}
