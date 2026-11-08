using System.IO;
using SkiaSharp;

namespace AIHub.Services.OfficeEditor;

internal sealed record OfficeSlideCanvasModel(double Width, double Height, IReadOnlyList<OfficeSlideObject> Objects)
{
    internal Action<string, OfficeSlidePicture?>? Insert { get; init; }
}

internal readonly record struct OfficeSlideBounds(double X, double Y, double Width, double Height)
{
    internal bool IsValid => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width)
        && double.IsFinite(Height) && Width > 0 && Height > 0 && double.IsFinite(X + Width) && double.IsFinite(Y + Height);
}

internal sealed class OfficeSlideObject(string id, string name, bool isPicture, Func<OfficeSlideBounds> bounds,
    Action<OfficeSlideBounds> setBounds, Action<OfficeSlidePicture>? replacePicture = null, Func<bool>? showEmptyOutline = null)
{
    internal string Id => id;
    internal string Name => name;
    internal bool IsPicture => isPicture;
    internal bool CanReplacePicture => replacePicture != null;
    internal bool ShowEmptyOutline => showEmptyOutline?.Invoke() == true;
    internal Action? Delete { get; init; }
    internal Action<double>? Rotate { get; init; }
    internal Func<IReadOnlyList<OfficeNativeBlock>>? TextBlocks { get; init; }
    internal Action<bool>? ChangeOrder { get; init; }
    internal object Identity { get; init; } = id;
    internal Func<double>? Angle { get; init; }
    internal OfficeSlideBounds Bounds => bounds();
    internal void SetBounds(OfficeSlideBounds value)
    {
        if (!value.IsValid) throw new ArgumentOutOfRangeException(nameof(value));
        if (value != Bounds) setBounds(value);
    }
    internal void ReplacePicture(OfficeSlidePicture value)
    {
        if (replacePicture == null) throw new InvalidOperationException();
        replacePicture(value);
    }
}

internal sealed record OfficeSlidePicture(byte[] Bytes, string Extension, string MimeType)
{
    // Decode the selected file before changing a relationship. Both formats can
    // embed the original encoded bytes, including transparency and metadata.
    internal static OfficeSlidePicture Load(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length is <= 0 or > 64 * 1024 * 1024) throw Invalid();
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        using var codec = SKCodec.Create(new SKMemoryStream(bytes));
        if (codec == null || codec.Info.Width <= 0 || codec.Info.Height <= 0 ||
            (long)codec.Info.Width * codec.Info.Height > 48_000_000) throw Invalid();
        var result = codec.EncodedFormat switch
        {
            SKEncodedImageFormat.Png => new OfficeSlidePicture(bytes, ".png", "image/png"),
            SKEncodedImageFormat.Jpeg => new OfficeSlidePicture(bytes, ".jpg", "image/jpeg"),
            _ => throw Invalid()
        };
        using var bitmap = new SKBitmap(new SKImageInfo(codec.Info.Width, codec.Info.Height));
        if (bitmap.GetPixels() == IntPtr.Zero || codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success)
            throw Invalid();
        return result;
    }

    private static InvalidDataException Invalid() => new("OfficeEditor.SlideInvalidPicture");
}
