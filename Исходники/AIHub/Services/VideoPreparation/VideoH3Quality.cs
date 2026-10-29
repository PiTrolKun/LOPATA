namespace AIHub.Services.VideoPreparation;

/// <summary>A user-facing quality scale, not a percentage of preserved information.</summary>
internal static class VideoH3Quality
{
    private static readonly (int Percent, int Crf)[] Points = [(0, 51), (40, 35), (60, 28), (70, 23), (80, 20), (85, 19), (90, 18), (95, 16), (100, 0)];
    internal static int ToPercent(int crf)
    {
        crf = Math.Clamp(crf, 0, 51);
        for (var i = 1; i < Points.Length; i++)
            if (crf >= Points[i].Crf)
                return Interpolate(crf, Points[i].Crf, Points[i - 1].Crf, Points[i].Percent, Points[i - 1].Percent);
        return 100;
    }
    internal static int ToCrf(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        for (var i = 1; i < Points.Length; i++)
            if (percent <= Points[i].Percent)
                return Interpolate(percent, Points[i - 1].Percent, Points[i].Percent, Points[i - 1].Crf, Points[i].Crf);
        return 0;
    }
    private static int Interpolate(int value, int start, int end, int from, int to) =>
        (int)Math.Round(from + (to - from) * (value - start) / (double)(end - start));
}
