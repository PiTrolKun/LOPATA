using System.IO;
using System.Text;

namespace AIHub.Services;

public static class MusicSourceText
{
    public const int MaximumCharacters = 100_000;
    public static string Read(string path)
    {
        if (new FileInfo(path).Length > 2_000_000) throw new InvalidDataException("Music.Transform.SourceTextTooLarge");
        var data = File.ReadAllBytes(path); string text;
        if (data.AsSpan().StartsWith(new byte[] { 255, 254 })) text = new UnicodeEncoding(false, true, true).GetString(data, 2, data.Length - 2);
        else if (data.AsSpan().StartsWith(new byte[] { 254, 255 })) text = new UnicodeEncoding(true, true, true).GetString(data, 2, data.Length - 2);
        else {
            var skip = data.AsSpan().StartsWith(new byte[] { 239, 187, 191 }) ? 3 : 0;
            try { text = new UTF8Encoding(false, true).GetString(data, skip, data.Length - skip); }
            catch (DecoderFallbackException) {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); text = Encoding.GetEncoding(1251).GetString(data);
            }
        }
        Validate(text); return text;
    }
    public static void Validate(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("Music.Transform.SourceTextEmpty");
        if (text.Length > MaximumCharacters) throw new InvalidDataException("Music.Transform.SourceTextTooLarge");
        if (text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'))) throw new InvalidDataException("Music.Transform.SourceTextFailed");
    }
}
