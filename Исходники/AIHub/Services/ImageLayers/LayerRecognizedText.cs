using System.Text.Json.Serialization;

namespace AIHub.Services.ImageLayers;

[JsonConverter(typeof(JsonStringEnumConverter<LayerTextKind>))]
internal enum LayerTextKind { Area, Word, Glyph }

/// <summary>Text in reading order, with the authored appearance stored in the layer's image-space mask.</summary>
internal sealed record LayerRecognizedText(LayerTextKind Kind, string Value, int Order, int Line, double Confidence)
{
    internal bool IsValid() => Enum.IsDefined(Kind) && Order >= 0 && Line >= 0
        && double.IsFinite(Confidence) && Confidence is >= 0 and <= 1
        && !string.IsNullOrWhiteSpace(Value) && Value.Length <= 8192
        && Value.All(character => !char.IsControl(character) || Kind == LayerTextKind.Area && character == '\n')
        && (Kind == LayerTextKind.Area || !Value.Any(char.IsWhiteSpace));

    internal static bool IsTextLabel(string name)
    {
        var first = name.Split([' ', ':', '·', '—', '«'], 2, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.ToLowerInvariant();
        return first is "текст" or "подпись" or "подзаголовок" or "надпись" or "слово" or "аббревиатура"
            or "логотип" or "text" or "caption" or "subtitle" or "word" or "lettering" or "logo";
    }
}
