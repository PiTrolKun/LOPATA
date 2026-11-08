using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Text.RegularExpressions;
using System.Globalization;
using AIHub.Services.OfficeReader;

namespace AIHub.Services.OfficeEditor;

internal static class OfficeRephraseText
{
    internal static string Input(string text, string style) => JsonSerializer.Serialize(new { SourceText = text, RequestedStyle = style },
        new JsonSerializerOptions { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) });

    internal static void Validate(string original, string rewritten)
    {
        if (!ReaderTranslationProtocol.Digits(original).Select(NormalizeTime).Order()
            .SequenceEqual(ReaderTranslationProtocol.Digits(rewritten).Select(NormalizeTime).Order()))
            throw new InvalidDataException("OfficeRephrase.ChangedNumbers");
    }

    private static string NormalizeTime(string value)
    {
        var hour = Regex.Match(value, @"^([01]?[0-9]|2[0-3]):00$");
        return hour.Success ? int.Parse(hour.Groups[1].Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) : value;
    }
}
