using System.IO;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace AIHub.Services.AnimatedAssets;

internal static partial class AnimationRenderer
{
    private sealed class BlinkFrames
    {
        private readonly byte[] _open;
        private readonly byte[] _closed;
        private readonly byte[] _pixels;
        private readonly SKRectI _area;

        public BlinkFrames(SKBitmap open, SKBitmap closed, SKRect content, AnimationEyeRegion? eye)
        {
            _open = open.Bytes;
            _closed = closed.Bytes;
            _pixels = new byte[_open.Length];
            _area = eye is null ? InferArea(open.Width, open.Height) : new(
                Math.Clamp((int)Math.Floor(content.Left + eye.X * content.Width), 0, open.Width),
                Math.Clamp((int)Math.Floor(content.Top + eye.Y * content.Height), 0, open.Height),
                Math.Clamp((int)Math.Ceiling(content.Left + (eye.X + eye.Width) * content.Width), 0, open.Width),
                Math.Clamp((int)Math.Ceiling(content.Top + (eye.Y + eye.Height) * content.Height), 0, open.Height));
            if (_area.Width <= 0 || _area.Height <= 0)
                throw new InvalidDataException("AnimatedAssets.Error.EyeArea");
        }

        public void Draw(SKBitmap frame, float shutter)
        {
            if (shutter <= 0)
            {
                Marshal.Copy(_open, 0, frame.GetPixels(), _open.Length);
                frame.NotifyPixelsChanged();
                return;
            }

            _open.CopyTo(_pixels, 0);
            var feather = Math.Max(1, _area.Height * .12f);
            var lid = _area.Top - feather + shutter * (_area.Height + 2 * feather);
            for (var row = _area.Top; row < _area.Bottom; row++)
            {
                var visible = Smooth(.5f + (lid - row) / (2 * feather));
                var start = row * frame.RowBytes + _area.Left * 4;
                var end = row * frame.RowBytes + _area.Right * 4;
                // Both images are premultiplied, so the feather does not smear transparent edges.
                for (var offset = start; offset < end; offset++)
                    _pixels[offset] = (byte)MathF.Round(_open[offset] + (_closed[offset] - _open[offset]) * visible);
            }
            Marshal.Copy(_pixels, 0, frame.GetPixels(), _pixels.Length);
            frame.NotifyPixelsChanged();
        }

        private SKRectI InferArea(int width, int height)
        {
            var left = width;
            var right = 0;
            var top = height;
            var bottom = 0;
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var offset = (y * width + x) * 4;
                    var change = Math.Abs(_open[offset] - _closed[offset])
                        + Math.Abs(_open[offset + 1] - _closed[offset + 1])
                        + Math.Abs(_open[offset + 2] - _closed[offset + 2])
                        + Math.Abs(_open[offset + 3] - _closed[offset + 3]);
                    if (change <= 32)
                        continue;
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x + 1);
                    bottom = Math.Max(bottom, y + 1);
                }
            return new(left, top, right, bottom);
        }
    }

    private static class AnimationVisualEffects
    {
        public static void Draw(SKCanvas canvas, SKRect content, IReadOnlyList<TimedEffect> effects, double time)
        {
            foreach (var timed in effects)
            {
                var effect = timed.Effect;
                var elapsed = time - timed.Time;
                if (!double.IsFinite(effect.Duration) || elapsed < 0 || elapsed >= effect.Duration)
                    continue;
                var x = content.Left + content.Width * Coordinate(effect.X, .5f);
                var y = content.Top + content.Height * Coordinate(effect.Y, .55f);
                var unit = Math.Min(content.Width, content.Height) / 18;
                var progress = (float)(elapsed / effect.Duration);
                if (effect.Kind == "lightning")
                    DrawLightning(canvas, x, y, unit, progress);
                else if (effect.Kind == "notes")
                    DrawNotes(canvas, x, y, unit, progress);
            }
        }

        private static float Coordinate(double? coordinate, float fallback) =>
            coordinate is { } value && double.IsFinite(value) ? (float)Math.Clamp(value, 0, 1) : fallback;

        private static void DrawLightning(SKCanvas canvas, float x, float y, float unit, float progress)
        {
            var alpha = (byte)Math.Round(245 * (1 - Smooth(progress)));
            using var points = new SKPath();
            points.MoveTo(x - unit * .25f, y - unit * 1.7f);
            points.LineTo(x + unit * .55f, y - unit * .35f);
            points.LineTo(x - unit * .24f, y + unit * .07f);
            points.LineTo(x + unit * .42f, y + unit * 1.15f);
            using var paint = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round,
                Color = new SKColor(255, 152, 73, (byte)(alpha / 2)), StrokeWidth = .37f * unit
            };
            canvas.DrawPath(points, paint);
            paint.Color = new SKColor(255, 251, 206, alpha);
            paint.StrokeWidth = .16f * unit;
            canvas.DrawPath(points, paint);
        }

        private static void DrawNotes(SKCanvas canvas, float x, float y, float unit, float progress)
        {
            var lift = (1.2f + Smooth(progress) * 2.1f) * unit;
            var spread = (1.4f + progress * .7f) * unit;
            var alpha = (byte)Math.Round(240 * (1 - Smooth(progress)));
            using var paint = new SKPaint
            {
                IsAntialias = true, Color = new SKColor(255, 244, 178, alpha), StrokeWidth = .12f * unit,
                StrokeCap = SKStrokeCap.Round
            };
            DrawNote(x - spread, y - lift);
            DrawNote(x + spread, y - lift - .55f * unit);

            void DrawNote(float cx, float cy)
            {
                canvas.DrawOval(SKRect.Create(cx - .22f * unit, cy - .13f * unit, .44f * unit, .27f * unit), paint);
                canvas.DrawLine(cx + .17f * unit, cy, cx + .17f * unit, cy - .87f * unit, paint);
                canvas.DrawLine(cx + .17f * unit, cy - .87f * unit, cx + .56f * unit, cy - .54f * unit, paint);
            }
        }
    }
}
