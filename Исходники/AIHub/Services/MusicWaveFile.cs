using System.IO;

namespace AIHub.Services;

/// <summary>Validate the native worker's PCM16 stereo output before adding it to the track library.</summary>
public static class MusicWaveFile
{
    public static TimeSpan ReadDuration(string path)
    {
        using var stream = File.OpenRead(path); using var reader = new BinaryReader(stream);
        if (stream.Length < 44 || reader.ReadUInt32() != 0x46464952 || reader.ReadUInt32() + 8L != stream.Length
            || reader.ReadUInt32() != 0x45564157) throw new InvalidDataException("Invalid or incomplete RIFF audio file.");
        var format = false; long bytes = -1;
        while (stream.Position < stream.Length)
        {
            if (stream.Length - stream.Position < 8) throw new InvalidDataException("Truncated WAV chunk header.");
            var kind = reader.ReadUInt32(); var size = reader.ReadUInt32(); var end = checked(stream.Position + size);
            if (end > stream.Length) throw new InvalidDataException("Truncated WAV chunk.");
            if (kind == 0x20746d66)
            {
                if (format || size < 16 || reader.ReadUInt16() != 1 || reader.ReadUInt16() != 2
                    || reader.ReadUInt32() != 48000 || reader.ReadUInt32() != 192000
                    || reader.ReadUInt16() != 4 || reader.ReadUInt16() != 16)
                    throw new InvalidDataException("Expected YuE2 PCM16 stereo 48 kHz audio.");
                format = true;
            }
            else if (kind == 0x61746164)
            {
                if (bytes >= 0 || size == 0 || size % 4 != 0) throw new InvalidDataException("Invalid WAV sample data.");
                bytes = size;
            }
            stream.Position = end + (size & 1);
            if (stream.Position > stream.Length) throw new InvalidDataException("Missing WAV chunk padding.");
        }
        if (!format || bytes <= 0) throw new InvalidDataException("WAV format or audio samples are missing.");
        return TimeSpan.FromSeconds(bytes / 192000d);
    }
}
