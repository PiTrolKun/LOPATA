using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
using AIHub.Services.AnimatedAssets;
using SkiaSharp;

namespace AIHub.Tests;

[TestClass]
public sealed class AnimatedAssetsRenderingTests
{
    [TestMethod]
    [DataRow("jump")]
    [DataRow("sway")]
    [DataRow("blink")]
    public void QuickMotionsRetainAlphaAndReturnToTheFirstFrame(string motion)
    {
        var project = new AnimationProject { Reference = Artwork() };
        var card = new AnimationCard { Motion = motion, ClosedEye = Artwork(closed: true), EyeRegion = new(.3, .27, .4, .3) };

        var frames = AnimationRenderer.Render(project, card);
        var first = Pixels(frames[0]);

        Assert.AreEqual(50, frames.Count);
        Assert.IsTrue(frames.All(frame => frame.IsFrozen));
        CollectionAssert.AreEqual(first, Pixels(frames[^1]));
        Assert.IsFalse(first.SequenceEqual(Pixels(frames[23])));
        Assert.IsTrue(first.Where((_, offset) => offset % 4 == 3).Any(alpha => alpha is > 0 and < 255));
    }

    [TestMethod]
    public void EffectsAppearAtTheirPoseTimeAndLeaveTheLoopSeamUnchanged()
    {
        var project = new AnimationProject { Reference = Artwork() };
        var pose = new AnimationPose
        {
            Time = 1,
            Image = Artwork(closed: true),
            Effects = [new() { Kind = "lightning", Duration = .2, X = .2, Y = .45 }]
        };
        var card = new AnimationCard { Motion = "frames", Poses = [pose] };
        var effect = AnimationRenderer.Render(project, card);
        pose.Effects.Clear();
        var plain = AnimationRenderer.Render(project, card);
        var second = AnimationRenderer.GetFramesPerSecond(card);

        CollectionAssert.AreEqual(Pixels(plain[second - 1]), Pixels(effect[second - 1]));
        Assert.IsFalse(Pixels(plain[second]).SequenceEqual(Pixels(effect[second])));
        CollectionAssert.AreEqual(Pixels(plain[second + second / 5]), Pixels(effect[second + second / 5]));
        CollectionAssert.AreEqual(Pixels(effect[0]), Pixels(effect[^1]));
    }

    [TestMethod]
    public void DrawnTransitionUsesTheWholeIntervalAndShowsTheNearestKeySample()
    {
        var original = Artwork();
        var closed = Artwork(closed: true);
        var project = new AnimationProject { Reference = original };
        var pose = new AnimationPose { Time = .75, Image = closed };
        var card = new AnimationCard { Motion = "frames", Poses = [pose] };
        var frames = AnimationRenderer.Render(project, card);

        Assert.AreEqual(100, frames.Count);
        Assert.AreEqual(25, AnimationRenderer.GetFramesPerSecond(card, telegram: true));
        Assert.IsFalse(Pixels(frames[0]).SequenceEqual(Pixels(frames[5])));
        Assert.IsFalse(Pixels(frames[38]).SequenceEqual(Pixels(frames[45])));

        project.Reference = closed;
        var expectedKey = AnimationRenderer.Render(project, card)[0];
        CollectionAssert.AreEqual(Pixels(expectedKey), Pixels(frames[38]));
        CollectionAssert.AreEqual(Pixels(frames[0]), Pixels(frames[^1]));
    }

    [TestMethod]
    public async Task ExportKeepsAnimatedWebpTimingAndTheSelectedStillInPngWithProvenance()
    {
        var temporary = Directory.CreateTempSubdirectory("lopata-animated-assets-");
        var webp = Path.Combine(temporary.FullName, "reaction.webp");
        var png = Path.Combine(temporary.FullName, "selected.png");
        try
        {
            var pose = new AnimationPose { Time = 1, Image = Artwork(closed: true) };
            var card = new AnimationCard { Motion = "frames", Poses = [pose] };
            var project = new AnimationProject { Reference = Artwork(), PngPoseId = pose.Id, Cards = [card] };

            await AnimationRenderer.ExportAsync(webp, project, card, "");
            await AnimationRenderer.ExportAsync(png, project, card, "");

            var animated = File.ReadAllBytes(webp);
            using var data = SKData.CreateCopy(animated);
            using var codec = SKCodec.Create(data);
            Assert.IsNotNull(codec);
            Assert.IsTrue(codec.FrameCount > 3);
            Assert.AreEqual(2000, codec.FrameInfo.Sum(frame => frame.Duration));
            Assert.IsTrue(codec.FrameInfo.Any(frame => frame.Duration == 20));
            Assert.IsTrue(codec.FrameInfo.Any(frame => frame.HasAlphaWithinBounds));
            StringAssert.Contains(Encoding.UTF8.GetString(animated), "animated-assets/webp");
            using var expected = SKBitmap.Decode(pose.Image!);
            using var actual = SKBitmap.Decode(png);
            CollectionAssert.AreEqual(expected.Bytes, actual.Bytes);
            StringAssert.Contains(Encoding.UTF8.GetString(File.ReadAllBytes(png)), "animated-assets/png");
        }
        finally
        {
            File.Delete(webp);
            File.Delete(png);
            temporary.Delete();
        }
    }

    [TestMethod]
    public void GeneratedReactionKeepsTheSourceFramingDespiteModelDimensionRounding()
    {
        static byte[] Solid(int width, int height, SKColor color)
        {
            using var bitmap = new SKBitmap(width, height);
            bitmap.Erase(color);
            using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            return encoded.ToArray();
        }

        var project = new AnimationProject { Reference = Solid(251, 201, SKColors.Gray) };
        var pose = new AnimationPose
        {
            Image = Solid(768, 640, SKColors.SlateGray), Time = 2.5,
            Models = [new("artist", "qwen-image-21-turbo-int8")]
        };
        var card = new AnimationCard { Kind = "reaction", Motion = "frames", Duration = 5, Poses = [pose] };

        var rendered = AnimationRenderer.Render(project, card);
        var keyIndex = (int)(pose.Time * AnimationRenderer.GetFramesPerSecond(card));
        var keyFrame = rendered[keyIndex];
        var middleLeftAlpha = keyFrame.PixelWidth * (keyFrame.PixelHeight / 2) * 4 + 3;
        Assert.AreEqual(byte.MaxValue, Pixels(keyFrame)[middleLeftAlpha]);
        CollectionAssert.AreEqual(Pixels(rendered[0]), Pixels(rendered[^1]));

        pose.Models.Clear();
        var manualFrames = AnimationRenderer.Render(project, card);
        Assert.AreEqual((byte)0, Pixels(manualFrames[keyIndex])[middleLeftAlpha]);
    }

    [TestMethod]
    public async Task GifExportKeepsExactCentisecondTimingLoopAndModelProvenance()
    {
        var temporary = Directory.CreateTempSubdirectory("lopata-animated-gif-");
        var path = Path.Combine(temporary.FullName, "reaction.gif");
        try
        {
            var pose = new AnimationPose
            {
                Time = 1, Image = Artwork(closed: true), Models = [new("artist", "gif-test-artist", "fixture-r1")]
            };
            var card = new AnimationCard { Motion = "frames", Duration = 2.75, Poses = [pose] };
            var project = new AnimationProject { Reference = Artwork(), Cards = [card] };
            await AnimationRenderer.ExportAsync(path, project, card, "");

            var encoded = File.ReadAllBytes(path);
            Assert.IsTrue(encoded.AsSpan().StartsWith("GIF89a"u8));
            StringAssert.Contains(Encoding.UTF8.GetString(encoded), "NETSCAPE2.0");
            StringAssert.Contains(Encoding.UTF8.GetString(encoded), "animated-assets/gif");
            StringAssert.Contains(Encoding.UTF8.GetString(encoded), "gif-test-artist");
            using var codec = SKCodec.Create(path);
            Assert.IsNotNull(codec);
            Assert.AreEqual(-1, codec.RepetitionCount);
            Assert.IsTrue(codec.FrameCount > 3);
            Assert.AreEqual(2750, codec.FrameInfo.Sum(frame => frame.Duration));
            Assert.IsTrue(codec.FrameInfo.All(frame => frame.Duration >= 20 && frame.Duration % 10 == 0));
            using var first = ReadGifFrame(codec, 0);
            using var last = ReadGifFrame(codec, codec.FrameCount - 1);
            CollectionAssert.AreEqual(first.Bytes, last.Bytes);
            Assert.IsTrue(first.Bytes.Where((_, index) => index % 4 == 3).All(alpha => alpha is 0 or 255));
            Assert.AreEqual((byte)0, first.GetPixel(0, 0).Alpha);

            var fill = first.GetPixel(first.Width / 2, first.Height / 2);
            Assert.AreEqual((byte)255, fill.Alpha);
            Assert.IsTrue(Math.Abs(fill.Red - 251) <= 4 && Math.Abs(fill.Green - 136) <= 4 && Math.Abs(fill.Blue - 72) <= 4);
        }
        finally
        {
            File.Delete(path);
            temporary.Delete();
        }
    }

    [TestMethod]
    public async Task GifMovingArtworkClearsPixelsThatBecomeTransparent()
    {
        var temporary = Directory.CreateTempSubdirectory("lopata-animated-gif-jump-");
        var path = Path.Combine(temporary.FullName, "jump.gif");
        try
        {
            var card = new AnimationCard { Motion = "jump", Strength = 1 };
            var project = new AnimationProject { Reference = Artwork(), Quick = true, Cards = [card] };
            await AnimationRenderer.ExportAsync(path, project, card, "");

            using var codec = SKCodec.Create(path);
            Assert.IsNotNull(codec);
            var frames = codec.FrameInfo;
            var index = 0;
            var elapsed = 0;
            while (index < frames.Length - 1 && elapsed + frames[index].Duration <= 1000)
                elapsed += frames[index++].Duration;
            using var first = ReadGifFrame(codec, 0);
            using var raised = ReadGifFrame(codec, index);
            var from = first.Bytes;
            var to = raised.Bytes;
            var cleared = Enumerable.Range(0, first.Width * first.Height).Count(position =>
                from[position * 4 + 3] == 255 && to[position * 4 + 3] == 0);
            Assert.IsTrue(cleared > 100);
        }
        finally
        {
            File.Delete(path);
            temporary.Delete();
        }
    }

    private static SKBitmap ReadGifFrame(SKCodec codec, int index)
    {
        var info = codec.Info.WithColorType(SKColorType.Bgra8888).WithAlphaType(SKAlphaType.Unpremul);
        var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels(), new SKCodecOptions(index));
        if (result == SKCodecResult.Success)
            return bitmap;
        bitmap.Dispose();
        throw new InvalidDataException($"Could not decode GIF frame {index}: {result}.");
    }

    private static byte[] Pixels(BitmapSource image)
    {
        var stride = image.PixelWidth * 4;
        var bytes = new byte[image.PixelHeight * stride];
        image.CopyPixels(bytes, stride, 0);
        return bytes;
    }

    private static byte[] Artwork(bool closed = false)
    {
        using var bitmap = new SKBitmap(96, 88, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { IsAntialias = true, Color = new SKColor(251, 136, 72) };
        canvas.DrawCircle(48, 44, 32, paint);
        paint.Color = new SKColor(56, 24, 12);
        canvas.DrawOval(new SKRect(34, 24, 63, closed ? 28 : 41), paint);
        canvas.Flush();
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }
}
