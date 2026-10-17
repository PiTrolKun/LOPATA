using System.IO;

namespace AIHub.Services;

/// <summary>Built-in artwork; no model, network or user-folder dependency.</summary>
internal static class MusicCoverArt
{
    private static readonly Lazy<AudioCover> BuiltIn = new(() =>
    {
        using var source = typeof(MusicCoverArt).Assembly.GetManifestResourceStream("AIHub.MusicDefaultCover")
            ?? throw new InvalidDataException("Missing default music artwork.");
        using var buffer = new MemoryStream(); source.CopyTo(buffer);
        var bytes = buffer.ToArray();
        using var codec = SkiaSharp.SKCodec.Create(new SkiaSharp.SKMemoryStream(bytes));
        if (codec is null || codec.Info.Width != 1024 || codec.Info.Height != 1024
            || codec.EncodedFormat != SkiaSharp.SKEncodedImageFormat.Png)
            throw new InvalidDataException("Invalid default music artwork.");
        return new("image/png", 3, "LOPATA", bytes);
    });

    internal static AudioCover Default => BuiltIn.Value;

    internal static async Task VerifyAsync(MusicAudioRuntime runtime, string path, CancellationToken token, AudioCover? expected = null)
    {
        expected ??= Default;
        var metadata = await AudioConversionMetadata.ReadAsync(runtime, path, token);
        if (metadata.Covers.Count != 1 || metadata.Covers[0].Type != expected.Type
            || metadata.Covers[0].Mime != expected.Mime || !metadata.Covers[0].Data.AsSpan().SequenceEqual(expected.Data))
            throw new InvalidDataException("Encoded audio lost its default artwork.");
    }
}
