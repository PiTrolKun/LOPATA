namespace AIHub.Services;

public sealed record MusicTextMarkerChoice(string Text, string NameKey);

public static class MusicTextMarkerCatalog
{
    public static IReadOnlyList<MusicTextMarkerChoice> Choices(string category) => category switch
    {
        "Section" => [Choice("Intro"), Choice("Verse"), Choice("Chorus"), Choice("Bridge"), Choice("Outro")],
        "Delivery" => new[] { Choice("Spoken"), Choice("Softly"), Choice("Restrained"), Choice("Cold"), Choice("Sincere"), Choice("Ironic") }
            .Concat(MusicWishCatalog.Group("delivery").Select(x => new MusicTextMarkerChoice(x.Prompt, x.NameKey))).ToArray(),
        "Cue" => [Choice("Break"), Choice("Instrumental break"), Choice("Instrumental transition"), Choice("Instrumental ending"),
            Choice("Music stops"), Choice("Instruments fade to silence"), Choice("Four bars"), Choice("Clean stop")],
        _ => []
    };
    private static MusicTextMarkerChoice Choice(string text) => new(text, "Music.Text.Tag." + text.Replace(' ', '_'));
}
