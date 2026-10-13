namespace AIHub.Services;

internal static class MusicPoetryThoughtAppearance
{
    internal const int DefaultIncrease = 200;
    internal const int MaximumIncrease = 400;
    internal static bool IsValid(int increase) => increase is >= 0 and <= MaximumIncrease;
}
