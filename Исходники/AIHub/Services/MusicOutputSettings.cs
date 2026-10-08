using System.IO;
using System.Text.Json;

namespace AIHub.Services;

public enum MusicAudioFormat { Opus, Mp3, Flac, Wav }

public sealed record MusicOutputSettings
{
    public const int DefaultBitrate = 320;
    public MusicAudioFormat Format { get; init; } = MusicAudioFormat.Opus;
    public int Bitrate { get; init; } = DefaultBitrate;
    public MusicAudioFormat? AdditionalFormat { get; init; }
    public int AdditionalBitrate { get; init; } = DefaultBitrate;
    public static int[] Bitrates => [160, 192, 256, 320];
    public static string Extension(MusicAudioFormat format) => format switch {
        MusicAudioFormat.Opus => ".opus", MusicAudioFormat.Mp3 => ".mp3",
        MusicAudioFormat.Flac => ".flac", MusicAudioFormat.Wav => ".wav",
        _ => throw new InvalidDataException("Unknown music format.") };
    public static bool Lossy(MusicAudioFormat format) => format is MusicAudioFormat.Opus or MusicAudioFormat.Mp3;
    public void Validate()
    {
        _ = Extension(Format);
        if (AdditionalFormat is { } extra) { _ = Extension(extra); if (extra == Format) throw new InvalidDataException("Duplicate audio format."); }
        if (!Bitrates.Contains(Bitrate) || !Bitrates.Contains(AdditionalBitrate)) throw new InvalidDataException("Invalid audio bitrate.");
    }
}

public sealed class MusicOutputPreferences(string path)
{
    public static MusicOutputPreferences Default { get; } = new(Path.Combine(AppDataPaths.BaseDirectory, "Music", "output.json"));
    public MusicOutputSettings Load()
    {
        if (!File.Exists(path)) return new();
        var value = JsonSerializer.Deserialize<MusicOutputSettings>(File.ReadAllText(path)) ?? throw new InvalidDataException("Missing output settings.");
        value.Validate(); return value;
    }
    public void Save(MusicOutputSettings value)
    {
        value.Validate(); Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, value); file.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
