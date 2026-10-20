using System.Text.RegularExpressions;
using AIHub.Models;

namespace AIHub.Services.MusicLibrary;

public static class LyricLibraryDraft
{
    public static LyricEntry FromEditor(MusicProjectSnapshot snapshot, string text, Func<string, string> localize)
    {
        var wishes = snapshot.Wishes.ToPreferences();
        var genres = wishes.Selected("genres").Select(id => MusicWishCatalog.Genres.FirstOrDefault(g => g.Id == id)?.Name ?? id).ToArray();
        wishes.Select("genres", genres);
        var languages = snapshot.Wishes.Performers.Select(p => Language(p.Language)).Where(s => s != "und").Distinct().ToArray();
        var explicitLanguage = snapshot.Expert.TextValues.GetValueOrDefault("vocal_language", "");
        var language = Language(explicitLanguage);
        if (language == "und") language = languages.Length == 1 ? languages[0] : "und";
        return new() { Title = string.IsNullOrWhiteSpace(snapshot.Title) ? localize("Music.Library.Untitled") : snapshot.Title,
            Artist = snapshot.Artist, Text = text, Genre = string.Join("; ", genres),
            Style = MusicWishPrompt.Build(wishes), Language = language, Notes = snapshot.Comment,
            Models = LyricLibraryProvenance.KnownModels(snapshot), Origin = "editor snapshot; complete model history unknown" };
    }
    public static string Language(string value)
    {
        value = value.Trim();
        if (Regex.IsMatch(value, @"\A[a-z]{2,3}(?:-[A-Za-z]{2,8})?\z") && value is not ("auto" or "all")) return value;
        return value.ToLowerInvariant() switch {
            "russian" or "русский" => "ru", "english" or "английский" => "en",
            "chinese" or "китайский" => "zh", "japanese" or "японский" => "ja",
            "korean" or "корейский" => "ko", "french" or "французский" => "fr",
            "german" or "немецкий" => "de", "spanish" or "испанский" => "es",
            "italian" or "итальянский" => "it", "portuguese" or "португальский" => "pt",
            "arabic" or "арабский" => "ar", "hindi" or "хинди" => "hi", _ => "und" };
    }
    public static LyricEntry FillEmptyMetadata(LyricEntry target, LyricEntry source) => target with {
        Artist = Empty(target.Artist, source.Artist), Author = Empty(target.Author, source.Author),
        Genre = Empty(target.Genre, source.Genre), Style = Empty(target.Style, source.Style),
        Tags = Empty(target.Tags, source.Tags), Language = target.Language == "und" ? source.Language : target.Language };
    private static string Empty(string target, string source) => string.IsNullOrWhiteSpace(target) ? source : target;
}
