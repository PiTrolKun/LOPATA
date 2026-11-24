using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;
using Color = System.Windows.Media.Color;

namespace AIHub.Services.AnimatedAssets;

internal static partial class AnimationRenderer
{
    private const byte GifTransparentIndex = 255;
    private const byte GifAlphaThreshold = 128;
    private const int GifMinimumDelayCs = 2;

    private static void ExportGif(string output, FrameSequence sequence, double duration, OutputProvenance facts,
        CancellationToken token)
    {
        var width = sequence.Size.Width;
        var height = sequence.Size.Height;
        var pixelCount = checked(width * height);
        using var straight = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var pixels = new byte[straight.ByteCount];

        void ReadFrame(int index)
        {
            token.ThrowIfCancellationRequested();
            using var source = sequence.Draw(index).PeekPixels();
            if (!source.ReadPixels(straight.Info, straight.GetPixels(), straight.RowBytes))
                throw new InvalidDataException("AnimatedAssets.Error.Encode");
            Marshal.Copy(straight.GetPixels(), pixels, 0, pixels.Length);
        }

        var colors = CollectGifColors(sequence.Count, width, height, pixels, ReadFrame);
        var colorTable = new byte[256 * 3];
        for (var index = 0; index < colors.Count; index++)
        {
            var color = colors[index];
            colorTable[index * 3] = color.R;
            colorTable[index * 3 + 1] = color.G;
            colorTable[index * 3 + 2] = color.B;
        }
        var indexTable = BuildGifIndexTable(colors, token);

        var durationCs = (int)Math.Round(duration * 100, MidpointRounding.AwayFromZero);
        // GIF's clock is measured in centiseconds. Delays below 2 cs are clamped by many viewers.
        var count = Math.Min(sequence.Count, durationCs / GifMinimumDelayCs);
        using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var encoder = new GifStreamEncoder(stream, width, height, true, facts.Json(), colorTable, GifTransparentIndex);
        var previous = new byte[pixelCount];
        var current = new byte[pixelCount];
        var pending = 0;
        var boundary = 0;
        for (var index = 0; index < count; index++)
        {
            ReadFrame(index == count - 1 ? sequence.Count - 1 : index);
            for (var pixel = 0; pixel < pixelCount; pixel++)
            {
                var offset = pixel * 4;
                current[pixel] = pixels[offset + 3] < GifAlphaThreshold ? GifTransparentIndex
                    : indexTable[(pixels[offset + 2] >> 3) << 10 | (pixels[offset + 1] >> 3) << 5 | pixels[offset] >> 3];
            }
            var nextBoundary = ((index + 1) * durationCs + count / 2) / count;
            var delay = nextBoundary - boundary;
            if (pending != 0 && !current.AsSpan().SequenceEqual(previous))
            {
                encoder.Add(previous, pending, token);
                pending = 0;
            }
            (previous, current) = (current, previous);
            pending += delay;
            boundary = nextBoundary;
        }
        encoder.Add(previous, pending, token);
        encoder.Finish();
    }

    private static IList<Color> CollectGifColors(int frameCount, int width, int height, byte[] pixels, Action<int> readFrame)
    {
        const int atlasWidth = 256;
        const int sampleLimit = 65_536;
        var atlas = new byte[sampleLimit * 4];
        var frameSamples = Math.Min(frameCount, 24);
        var step = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(width * height * (double)frameSamples / sampleLimit)));
        var count = 0;
        for (var frame = 0; frame < frameSamples && count < sampleLimit; frame++)
        {
            readFrame(frame * (frameCount - 1) / (frameSamples - 1));
            var started = count;
            for (var y = frame % step; y < height && count < sampleLimit; y += step)
                for (var x = (frame + y / step) % step; x < width && count < sampleLimit; x += step)
                    AddSample((y * width + x) * 4);
            if (count == started)
                for (var offset = 0; offset < pixels.Length && count == started; offset += 4)
                    AddSample(offset);
        }

        if (count == 0)
            return [Colors.Black];
        var heightInRows = (count + atlasWidth - 1) / atlasWidth;
        for (var pixel = count; pixel < heightInRows * atlasWidth; pixel++)
            Buffer.BlockCopy(atlas, (count - 1) * 4, atlas, pixel * 4, 4);
        var sample = BitmapSource.Create(atlasWidth, heightInRows, 96, 96, PixelFormats.Bgr32, null, atlas, atlasWidth * 4);
        return new BitmapPalette(sample, GifTransparentIndex).Colors;

        void AddSample(int offset)
        {
            if (pixels[offset + 3] < GifAlphaThreshold)
                return;
            Buffer.BlockCopy(pixels, offset, atlas, count * 4, 3);
            count++;
        }
    }

    private static byte[] BuildGifIndexTable(IList<Color> colors, CancellationToken token)
    {
        var table = new byte[1 << 15];
        for (var index = 0; index < table.Length; index++)
        {
            if ((index & 255) == 0)
                token.ThrowIfCancellationRequested();
            var r = (index >> 10) * 8 + 4;
            var g = (index >> 5 & 31) * 8 + 4;
            var b = (index & 31) * 8 + 4;
            var best = int.MaxValue;
            for (var candidate = 0; candidate < colors.Count; candidate++)
            {
                var color = colors[candidate];
                var dr = r - color.R;
                var dg = g - color.G;
                var db = b - color.B;
                var distance = 3 * dr * dr + 4 * dg * dg + 2 * db * db;
                if (distance >= best) continue;
                best = distance;
                table[index] = (byte)candidate;
            }
        }
        return table;
    }
}
