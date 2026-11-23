namespace AIHub.Services.NeuroConstructor;

internal static class NeuroProfiles
{
    internal static bool Full(string? composition) => composition is "medium" or "heavy";
    internal static string Id(string composition) => Full(composition) ? composition : "light";
}
