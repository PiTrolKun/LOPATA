using System.IO;
using System.Text.Json.Serialization;

namespace AIHub.Services.VideoConverter;

[JsonConverter(typeof(JsonStringEnumConverter<ConversionCodec>))]
internal enum ConversionCodec { Av1, Hevc, Avc, Vp9, Ffv1 }
[JsonConverter(typeof(JsonStringEnumConverter<ConversionContainer>))]
internal enum ConversionContainer { Mp4, Mkv, WebM }
[JsonConverter(typeof(JsonStringEnumConverter<ConversionExecutor>))]
internal enum ConversionExecutor { Auto, Cpu, Gpu }
[JsonConverter(typeof(JsonStringEnumConverter<ConversionMode>))]
internal enum ConversionMode { Percent, Size, Quality, Bitrate }

internal sealed record ConversionOptions
{
    public ConversionCodec Codec { get; init; } = ConversionCodec.Av1;
    public ConversionContainer Container { get; init; } = ConversionContainer.Mp4;
    public ConversionExecutor Executor { get; init; }
    public ConversionMode Mode { get; init; }
    public int Reduction { get; init; } = 10;
    public int LimitMb { get; init; } = 100;
    public int Quality { get; init; } = 3;
    public int BitrateKbps { get; init; } = 4000;
    public int ShortEdge { get; init; }
    public int FramesPerSecond { get; init; }
    public bool PictureMayChange { get; init; }
    public bool AudioMayChange { get; init; }
    public bool MotionMayChange { get; init; }
    public bool OmitIncompatibleTracks { get; init; }
    public bool Archive { get; init; }
    public string OutputDirectory { get; init; } = "";

    internal static readonly string[] Extensions = [".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi"];
    internal string Extension => Container switch { ConversionContainer.Mp4 => ".mp4", ConversionContainer.WebM => ".webm", _ => ".mkv" };
    internal string VideoCodec => Codec switch
    {
        ConversionCodec.Av1 => "av1", ConversionCodec.Hevc => "hevc", ConversionCodec.Avc => "h264",
        ConversionCodec.Vp9 => "vp9", _ => "ffv1"
    };
    internal static ConversionContainer[] Containers(ConversionCodec codec) => codec switch
    {
        ConversionCodec.Av1 => [ConversionContainer.Mp4, ConversionContainer.Mkv, ConversionContainer.WebM],
        ConversionCodec.Hevc or ConversionCodec.Avc => [ConversionContainer.Mp4, ConversionContainer.Mkv],
        ConversionCodec.Vp9 => [ConversionContainer.WebM, ConversionContainer.Mkv],
        _ => [ConversionContainer.Mkv]
    };
    internal static bool Accepts(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    internal long? Limit(long bytes) => Mode switch
    {
        ConversionMode.Percent => Math.Max(1, (long)Math.Floor(bytes * (100 - Reduction) / 100d)),
        ConversionMode.Size => LimitMb * 1_000_000L,
        _ => null
    };

    internal void Validate()
    {
        if (!Enum.IsDefined(Codec) || !Enum.IsDefined(Container) || !Containers(Codec).Contains(Container)
            || !Enum.IsDefined(Executor) || !Enum.IsDefined(Mode)
            || Codec == ConversionCodec.Ffv1 && Executor == ConversionExecutor.Gpu
            || Codec == ConversionCodec.Ffv1 && Mode is ConversionMode.Quality or ConversionMode.Bitrate
            || Reduction is < 0 or > 95 || LimitMb is < 1 or > 1_000_000 || Quality is < 1 or > 5
            || BitrateKbps is < 50 or > 200_000 || ShortEdge is < 0 or (> 0 and < 144) or > 4320
            || FramesPerSecond is < 0 or > 120 || OutputDirectory is null
            || OutputDirectory.Length > 0 && !Path.IsPathFullyQualified(OutputDirectory))
            throw new InvalidDataException("Проверьте формат, размер, качество и папку сохранения.");
        if (FramesPerSecond > 0 && !MotionMayChange)
            throw new InvalidDataException("Чтобы изменить число кадров в секунду, разрешите изменить плавность движения.");
    }
}
