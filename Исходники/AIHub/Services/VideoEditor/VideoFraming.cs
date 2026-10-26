namespace AIHub.Services.VideoEditor;

internal readonly record struct VideoFrameGeometry(int CropX, int CropY, int CropWidth, int CropHeight, int Width, int Height);
internal static class VideoFraming
{
    // The preview uses the same pixel crop and even-sized output geometry as FFmpeg.
    internal static VideoFrameGeometry Geometry(VideoProject p, VideoAsset a, VideoClip c)
    {
        var width = Math.Max(1, (int)Math.Floor(a.Width * (1 - c.CropLeft - c.CropRight)));
        var height = Math.Max(1, (int)Math.Floor(a.Height * (1 - c.CropTop - c.CropBottom)));
        var factor = Math.Min((double)p.Width / width, (double)p.Height / height) * c.Scale;
        static int Even(double size) => Math.Max(2, (int)Math.Floor(size / 2 + .5) * 2);
        return new((int)Math.Floor(a.Width * c.CropLeft), (int)Math.Floor(a.Height * c.CropTop), width, height, Even(width * factor), Even(height * factor));
    }
    internal static double FillScale(VideoProject p, VideoAsset a, VideoClip c)
    {
        var g = Geometry(p, a, c);
        var x = (double)p.Width / g.CropWidth; var y = (double)p.Height / g.CropHeight;
        return Math.Max(x, y) / Math.Min(x, y);
    }
}
