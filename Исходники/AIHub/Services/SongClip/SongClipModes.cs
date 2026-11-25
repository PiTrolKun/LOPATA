using System.IO;

namespace AIHub.Services.SongClip;

internal static class SongClipModes
{
    internal const string Montage = "montage";
    // Null is deliberately retained for byte-compatible historical connected inputs.
    internal static string? Normalize(string? mode) => mode switch
    {
        null or "" or "connected" => null,
        Montage => Montage,
        SongClipDeno.Mode => SongClipDeno.Mode,
        _ => throw new InvalidDataException("SongClip.InvalidSettings")
    };
    internal static bool IsMontage(string? mode) => Normalize(mode) is Montage or SongClipDeno.Mode;
    internal static bool IsMontage(SongClipAttempt attempt) => IsMontage(attempt.Settings.Motion.Mode);
    internal static bool IsDeno(SongClipAttempt attempt) => attempt.Settings.Motion.Mode == SongClipDeno.Mode;
}
