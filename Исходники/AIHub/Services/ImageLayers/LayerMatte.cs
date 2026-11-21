using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using SkiaSharp;

namespace AIHub.Services.ImageLayers;

internal sealed record LayerMatteReference(string Key, string Geometry, Rectangle Bounds);

internal sealed class LayerMatte
{
    private LayerMatte(byte[] png, string key, int width, int height)
    {
        Png = png;
        Key = key;
        Width = width;
        Height = height;
    }

    internal byte[] Png { get; }
    internal string Key { get; }
    internal int Width { get; }
    internal int Height { get; }
    internal const int MaxBytes = LayerSource.MaxInputBytes;

    internal static LayerMatte Load(byte[] png)
    {
        if (png.Length is 0 or > MaxBytes) throw new InvalidDataException("ImageLayers.InvalidProject");
        using var data = SKData.CreateCopy(png);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.EncodedFormat != SKEncodedImageFormat.Png || codec.FrameCount > 1
            || codec.Info.Width < 1 || codec.Info.Height < 1 || codec.Info.AlphaType == SKAlphaType.Opaque
            || (long)codec.Info.Width * codec.Info.Height > LayerSource.MaxPixels)
            throw new InvalidDataException("ImageLayers.InvalidProject");
        return new(png, Convert.ToHexStringLower(SHA256.HashData(png)), codec.Info.Width, codec.Info.Height);
    }
}

internal sealed partial class LayerDocument
{
    private readonly Dictionary<string, LayerMatte> _mattes = new(StringComparer.Ordinal);

    internal void Remember(LayerMatte matte) => _mattes.TryAdd(matte.Key, matte);
    internal LayerMatte Matte(string key) => _mattes[key];
    internal bool HasMatte(string key) => _mattes.ContainsKey(key);

    internal (LayerMatteReference Reference, LayerMatte Image)? ActiveMatte(Guid id,
        IReadOnlyDictionary<Guid, ImageLayer>? state = null)
    {
        state ??= Layers;
        if (state.GetValueOrDefault(id)?.Matte is not { } reference || reference.Geometry != Aggregate(id, state).Key)
            return null;
        return (reference, Matte(reference.Key));
    }

    internal LayerMatteReference AddMatte(LayerMatte image, LayerMask geometry, Rectangle bounds)
    {
        if (!ValidMatteBounds(bounds) || image.Width != bounds.Width || image.Height != bounds.Height
            || !geometry.Any || !bounds.Contains(geometry.Bounds(Source.Width)))
            throw new InvalidDataException("ImageLayers.InvalidProject");
        Remember(image);
        return new(image.Key, geometry.Key, bounds);
    }

    private bool ValidMatte(LayerMatteReference? matte) => matte is null
        || ValidDigest(matte.Key) && ValidDigest(matte.Geometry) && ValidMatteBounds(matte.Bounds)
        && _mattes.TryGetValue(matte.Key, out var image) && image.Width == matte.Bounds.Width && image.Height == matte.Bounds.Height;

    private bool ValidMatteBounds(Rectangle bounds) => bounds.X >= 0 && bounds.Y >= 0 && bounds.Width > 0 && bounds.Height > 0
        && bounds.Width <= Source.Width - bounds.X && bounds.Height <= Source.Height - bounds.Y;

    internal static bool ValidDigest(string? digest) => digest is { Length: 64 } && digest.All(Uri.IsHexDigit);
}
