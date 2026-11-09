using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Lopata.Collection;

internal static class CollectionMidiOrigin
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static CollectionOrigin? Read(Stream input)
    {
        if (input.Length > 8_000_000) return null;
        Span<byte> header = stackalloc byte[14]; input.ReadExactly(header);
        var size = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
        var tracks = BinaryPrimitives.ReadUInt16BigEndian(header[10..]);
        if (!header[..4].SequenceEqual("MThd"u8) || size is < 6 or > 1024 || size > input.Length - 8
            || BinaryPrimitives.ReadUInt16BigEndian(header[8..]) > 2 || tracks is 0 or > 256) return null;
        input.Position = 8 + size;
        Span<byte> chunk = stackalloc byte[8];
        for (var track = 0; track < tracks; track++)
        {
            input.ReadExactly(chunk);
            var length = BinaryPrimitives.ReadUInt32BigEndian(chunk[4..]);
            if (!chunk[..4].SequenceEqual("MTrk"u8) || length > input.Length - input.Position) return null;
            var end = input.Position + length;
            if (Track(input, end) is { } origin) return origin;
            input.Position = end;
        }
        return null;
    }

    private static CollectionOrigin? Track(Stream input, long end)
    {
        var running = 0;
        for (var count = 0; count < 200000 && input.Position < end; count++)
        {
            if (!Vlq(input, end, out _) || input.Position >= end) return null;
            var status = input.ReadByte(); var consumed = 0;
            if (status < 128) { if (running == 0) return null; status = running; consumed = 1; }
            else if (status < 240) running = status;
            else running = 0;
            if (status == 255)
            {
                if (input.Position >= end) return null;
                var type = input.ReadByte();
                if (!Vlq(input, end, out var length) || length > end - input.Position) return null;
                if (type == 1 && length <= CollectionOriginReader.MetadataLimit)
                {
                    var bytes = new byte[length]; input.ReadExactly(bytes);
                    if (Text(Utf8.GetString(bytes)) is { } origin) return origin;
                }
                else input.Position += length;
                if (type == 47) return null;
            }
            else if (status is 240 or 247)
            {
                if (!Vlq(input, end, out var length) || length > end - input.Position) return null;
                input.Position += length;
            }
            else if (status is >= 128 and < 240)
            {
                var bytes = (status & 240) is 192 or 208 ? 1 : 2;
                for (; consumed < bytes; consumed++) if (input.Position >= end || input.ReadByte() >= 128) return null;
            }
            else return null;
        }
        return null;
    }

    private static bool Vlq(Stream input, long end, out int value)
    {
        value = 0;
        for (var count = 0; count < 4 && input.Position < end; count++)
        {
            var b = input.ReadByte(); value = (value << 7) | (b & 127);
            if ((b & 128) == 0) return true;
        }
        return false;
    }

    private static CollectionOrigin? Text(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
            var value = document.RootElement;
            return value.ValueKind == JsonValueKind.Object && String(value, "app") == "LOPATA"
                && String(value, "operation") == "music.melody.export" && Number(value, "schema") == 1
                && Version(String(value, "version")) ? new("music/melody") : null;
        }
        catch (JsonException) { return null; }
    }

    internal static CollectionOrigin? Project(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
            var value = document.RootElement;
            if (value.ValueKind != JsonValueKind.Object || String(value, "App") != "LOPATA" || Number(value, "Schema") != 1
                || !Version(String(value, "Version")) || !value.TryGetProperty("Melody", out var melody)
                || melody.ValueKind != JsonValueKind.Object || !value.TryGetProperty("Created", out var created)
                || created.ValueKind != JsonValueKind.String || !created.TryGetDateTimeOffset(out _)
                || Number(melody, "Bpm") is < 40 or > 240 || Number(melody, "Numerator") is < 1 or > 12
                || Number(melody, "Denominator") is not (2 or 4 or 8)
                || !melody.TryGetProperty("Notes", out var notes) || notes.ValueKind != JsonValueKind.Array || notes.GetArrayLength() > 4096) return null;
            foreach (var note in notes.EnumerateArray())
                if (note.ValueKind != JsonValueKind.Object || Number(note, "Pitch") is < 0 or > 127
                    || Number(note, "Start") is < 0 or > 1_000_000 || Number(note, "Length") is < 60 or > 1_000_000
                    || Number(note, "Velocity") is < 1 or > 127 || !Guid.TryParseExact(String(note, "Id"), "N", out _)) return null;
            return new("music/melody");
        }
        catch (JsonException) { return null; }
    }

    private static bool Version(string? value) => value is { Length: > 0 and <= 128 } && char.IsAsciiDigit(value[0]) && !value.Any(char.IsControl);
    private static string? String(JsonElement value, string name) => value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    private static int Number(JsonElement value, string name) => value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out var number) ? number : -1;
}
