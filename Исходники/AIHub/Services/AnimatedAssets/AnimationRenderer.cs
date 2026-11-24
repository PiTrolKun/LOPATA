using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Services.ImageLayers;
using SkiaSharp;

namespace AIHub.Services.AnimatedAssets;

internal static partial class AnimationRenderer
{
    private const int DefaultFramesPerSecond = 25;
    private const int DrawnFramesPerSecond = 50;
    public const double MinimumDuration = 2;
    public const double MaximumDuration = 30;
    public const double TelegramMaximumDuration = 3;
    private const int PreviewLongEdge = 320;
    private const int PreviewByteBudget = 128 * 1024 * 1024;
    private const int ExportLongEdge = 512;
    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear);

    public static int GetFramesPerSecond(AnimationCard card, bool telegram = false) =>
        card.Motion == "frames" && !telegram ? DrawnFramesPerSecond : DefaultFramesPerSecond;

    public static BitmapSource LoadStill(byte[] bytes)
    {
        using var source = LoadSource(bytes);
        var size = GetDimensions(source, Math.Min(1536, Math.Max(source.Width, source.Height)));
        using var scaled = DrawFitted(source, size, out _);
        return ToPreview(scaled);
    }

    public static IReadOnlyList<BitmapSource> Render(AnimationProject project, AnimationCard card,
        CancellationToken cancellationToken = default)
    {
        var framesPerSecond = GetFramesPerSecond(card);
        var count = FrameCount(card.Duration, framesPerSecond);
        var edge = Math.Min(PreviewLongEdge, (int)Math.Sqrt(PreviewByteBudget / (4d * count)));
        using var sequence = new FrameSequence(project, card, edge, square: false, even: false, framesPerSecond, cancellationToken);
        var frames = new List<BitmapSource>(sequence.Count);
        for (var index = 0; index < sequence.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            frames.Add(index == sequence.Count - 1 ? frames[0] : ToPreview(sequence.Draw(index)));
        }
        return frames;
    }

    private static byte[] BaseImage(AnimationProject project, AnimationCard card) =>
        (card.Motion == "frames" && card.Kind is "reaction" or "sequence"
            ? project.Reference ?? card.Still
            : card.Still ?? project.Reference)
        ?? throw new InvalidDataException("AnimatedAssets.Error.ImageMissing");

    private static SKBitmap LoadSource(byte[] bytes)
    {
        var source = LayerSource.Load("animation.png", bytes);
        var bitmap = new SKBitmap(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        Marshal.Copy(source.Pixels, 0, bitmap.GetPixels(), source.Pixels.Length);
        bitmap.SetImmutable();
        return bitmap;
    }

    private static BitmapSource ToPreview(SKBitmap bitmap)
    {
        var source = BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Pbgra32,
            null, bitmap.GetPixels(), bitmap.ByteCount, bitmap.RowBytes);
        source.Freeze();
        return source;
    }

    private static SKSizeI GetDimensions(SKBitmap source, int longEdge, bool square = false, bool even = false)
    {
        if (square)
            return new(longEdge, longEdge);

        var fit = (double)longEdge / Math.Max(source.Width, source.Height);
        int Dimension(int original)
        {
            var size = Math.Max(1, (int)Math.Round(original * fit));
            return even ? Math.Max(2, size & ~1) : size;
        }
        return new(Dimension(source.Width), Dimension(source.Height));
    }

    private static SKBitmap NewBitmap(SKSizeI size) =>
        new(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul);

    private static int FrameCount(double duration, int framesPerSecond)
    {
        if (!double.IsFinite(duration) || duration is < MinimumDuration or > MaximumDuration)
            throw new InvalidDataException("AnimatedAssets.Error.Duration");
        return (int)Math.Round(duration * framesPerSecond);
    }

    private static SKBitmap DrawFitted(SKBitmap source, SKSizeI size, out SKRect area)
    {
        var fit = Math.Min((float)size.Width / source.Width, (float)size.Height / source.Height);
        var width = source.Width * fit;
        var height = source.Height * fit;
        area = SKRect.Create((size.Width - width) / 2, (size.Height - height) / 2, width, height);
        return DrawPlaced(source, size, area);
    }

    private static SKBitmap DrawPlaced(SKBitmap source, SKSizeI size, SKRect area, SKRect? clip = null)
    {
        var result = NewBitmap(size);
        using var canvas = new SKCanvas(result);
        using var image = SKImage.FromBitmap(source);
        canvas.Clear(SKColors.Transparent);
        if (clip is { } content) canvas.ClipRect(content);
        canvas.DrawImage(image, area, Sampling);
        canvas.Flush();
        return result;
    }

    private static float Smooth(float position)
    {
        position = Math.Clamp(position, 0, 1);
        return position * position * (3 - 2 * position);
    }

    private sealed class FrameSequence : IDisposable
    {
        private readonly SKBitmap _frame;
        private readonly SKCanvas _canvas;
        private readonly SKBitmap _start;
        private readonly SKImage _startImage;
        private readonly SKRect _content;
        private readonly SKPaint _blend = new();
        private readonly List<KeyPose> _poses = [];
        private readonly List<TimedEffect> _effects = [];
        private readonly BlinkFrames? _blink;
        private readonly double _duration;
        private readonly float _strength;
        private readonly string _motion;
        public int Count { get; }
        public int FramesPerSecond { get; }
        public SKSizeI Size { get; }

        public FrameSequence(AnimationProject project, AnimationCard card, int longEdge, bool square, bool even,
            int framesPerSecond, CancellationToken token)
        {
            _duration = card.Duration;
            FramesPerSecond = framesPerSecond;
            Count = FrameCount(_duration, framesPerSecond);
            _strength = double.IsFinite(card.Strength) ? (float)Math.Clamp(card.Strength, 0, 1) : .5f;
            _motion = card.Motion;
            token.ThrowIfCancellationRequested();
            using var source = LoadSource(BaseImage(project, card));
            Size = GetDimensions(source, longEdge, square, even);
            _start = DrawFitted(source, Size, out _content);
            _start.SetImmutable();
            _startImage = SKImage.FromBitmap(_start);
            _frame = NewBitmap(Size);
            _canvas = new SKCanvas(_frame);
            try
            {
                if (_motion == "frames")
                    AddPoses(card, token);
                if (_motion == "blink")
                {
                    using var closedSource = LoadSource(card.ClosedEye
                        ?? throw new InvalidDataException("AnimatedAssets.Error.ClosedEyeMissing"));
                    if (Math.Abs((double)source.Width / source.Height - (double)closedSource.Width / closedSource.Height) > .015)
                        throw new InvalidDataException("AnimatedAssets.Error.EyeRatio");
                    using var closed = DrawFitted(closedSource, Size, out _);
                    _blink = new(_start, closed, _content, card.EyeRegion);
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private void AddPoses(AnimationCard card, CancellationToken token)
        {
            var time = 0d;
            _poses.Add(new(time, _startImage));
            if (card.Poses.Count == 0)
                throw new InvalidDataException("AnimatedAssets.Error.PoseMissing");
            foreach (var pose in card.Poses.OrderBy(pose => pose.Time))
            {
                token.ThrowIfCancellationRequested();
                if (!double.IsFinite(pose.Time) || pose.Time <= time || pose.Time >= _duration)
                    throw new InvalidDataException("AnimatedAssets.Error.PoseTime");
                using var decoded = LoadSource(pose.Image ?? throw new InvalidDataException("AnimatedAssets.Error.PoseMissing"));
                using var fitted = FitPose(decoded, card.Kind, pose);
                fitted.SetImmutable();
                _poses.Add(new(pose.Time, SKImage.FromBitmap(fitted)));
                time = pose.Time;
                _effects.AddRange(pose.Effects.Select(effect => new TimedEffect(pose.Time, effect)));
            }
            _poses.Add(new(_duration, _startImage));
            AlignPoseSamples();
        }

        private void AlignPoseSamples()
        {
            // Keep the drawings visible at the closest samples. Leave densely packed legacy timestamps
            // intact when there is no distinct sample for each pose, so they still render and open.
            var samples = _poses.Select(pose => (int)Math.Round(pose.Time * FramesPerSecond)).ToArray();
            samples[^1] = Count - 1;
            for (var index = 1; index < samples.Length; index++)
                if (samples[index] <= samples[index - 1])
                    return;

            for (var index = 1; index < samples.Length; index++)
                _poses[index] = _poses[index] with { Time = (double)samples[index] / FramesPerSecond };
        }

        private SKBitmap FitPose(SKBitmap image, string kind, AnimationPose pose)
        {
            // Artist edits use block-aligned dimensions. Absorb small aspect changes inside the source framing.
            var aspectRatio = image.Width * _content.Height / (image.Height * _content.Width);
            if (kind is not ("reaction" or "sequence") || aspectRatio is < .94f or > 1.06f ||
                !pose.Models.Any(use => use.Role == "artist"))
                return DrawFitted(image, Size, out _);

            var fit = Math.Max(_content.Width / image.Width, _content.Height / image.Height);
            var width = fit * image.Width;
            var height = fit * image.Height;
            var placement = SKRect.Create(_content.MidX - width / 2, _content.MidY - height / 2, width, height);
            return DrawPlaced(image, Size, placement, _content);
        }

        public SKBitmap Draw(int index)
        {
            // The encoded first and last samples are identical. Interior samples use the playback clock.
            var time = index == Count - 1 ? _duration : (double)index / FramesPerSecond;
            var progress = (float)(time / _duration);
            if (_blink is not null)
            {
                var shutter = Smooth(1 - Math.Abs(progress - .46f) / .19f);
                _blink.Draw(_frame, shutter);
                return _frame;
            }

            _canvas.Clear(SKColors.Transparent);
            if (_motion == "jump" || _motion == "sway")
                DrawMovement(progress);
            else
            {
                if (_poses.Count == 0 || time == 0 || time == _duration)
                    _canvas.DrawImage(_startImage, 0, 0);
                else
                {
                    DrawPoses(time);
                    AnimationVisualEffects.Draw(_canvas, _content, _effects, time);
                }
            }
            _canvas.Flush();
            return _frame;
        }

        private void DrawMovement(float progress)
        {
            if (progress == 1)
                progress = 0;
            var phase = Math.Clamp((progress - .09f) / .82f, 0, 1);
            var lift = MathF.Sin(float.Pi * phase);
            var oscillation = MathF.Sin(float.Tau * phase);
            var jump = _motion == "jump";
            var size = 1 - _strength * (jump ? .27f : .24f);
            var squash = jump ? _strength * .12f * MathF.Pow(oscillation, 4) : 0;
            var dx = jump ? 0 : _strength * .035f * _content.Width * oscillation;
            var dy = jump ? -_strength * .22f * _content.Height * lift * lift : 0;
            var angle = _strength * (jump ? 4 : 12) * oscillation;
            _canvas.Save();
            _canvas.Translate(_content.MidX + dx, _content.MidY + dy);
            _canvas.RotateDegrees(angle);
            _canvas.Scale(size + squash * .6f, size - squash);
            _canvas.DrawImage(_startImage, -_content.MidX, -_content.MidY, Sampling);
            _canvas.Restore();
        }

        private void DrawPoses(double time)
        {
            var nextIndex = _poses.FindIndex(pose => pose.Time >= time);
            var current = _poses[nextIndex - 1];
            var next = _poses[nextIndex];

            // Use the whole interval. A moderate ease preserves motion through a dense set of poses.
            var progress = (float)((time - current.Time) / (next.Time - current.Time));
            var mix = (byte)Math.Round(255 * (progress + Smooth(progress)) / 2);
            _blend.BlendMode = SKBlendMode.Src;
            _blend.Color = SKColors.White.WithAlpha((byte)(255 - mix));
            _canvas.DrawImage(current.Image, 0, 0, _blend);
            _blend.BlendMode = SKBlendMode.Plus;
            _blend.Color = SKColors.White.WithAlpha(mix);
            _canvas.DrawImage(next.Image, 0, 0, _blend);
        }

        public void Dispose()
        {
            foreach (var pose in _poses)
                if (!ReferenceEquals(pose.Image, _startImage))
                    pose.Image.Dispose();
            _blend.Dispose();
            _canvas.Dispose();
            _frame.Dispose();
            _startImage.Dispose();
            _start.Dispose();
        }
    }

    private sealed record KeyPose(double Time, SKImage Image);
    private sealed record TimedEffect(double Time, AnimationEffect Effect);
}
