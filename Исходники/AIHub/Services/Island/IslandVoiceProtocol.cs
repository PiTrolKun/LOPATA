using System;
using System.IO;

namespace Lopata.Island;

internal sealed record IslandVoicePreferences(string RussianWake = "", string EnglishWake = "",
    string RussianReply = "", string EnglishReply = "")
{
    private const string FileName = "player-voice.json";
    internal static IslandVoicePreferences Read() => IslandPreferences.ReadJson<IslandVoicePreferences>(FileName) ?? new();
    internal void Save() => IslandPreferences.SaveJson(FileName, this);
    internal static bool IsRussian(string? language) =>
        language is not null && (language.Equals("ru", StringComparison.OrdinalIgnoreCase)
            || language.StartsWith("ru-", StringComparison.OrdinalIgnoreCase));
    internal static string DefaultWake(bool russian) => russian ? "Лопата приём" : "Lopata, come in";
    internal string Wake(bool russian) =>
        (russian ? RussianWake : EnglishWake) is { Length: > 0 and <= 120 } value
            ? value : DefaultWake(russian);
    internal string? Reply(bool russian)
    {
        var path = russian ? RussianReply : EnglishReply;
        return path is { Length: > 0 and <= 32767 } && Path.IsPathFullyQualified(path) && File.Exists(path)
            ? path : null;
    }
}

internal sealed record IslandVoiceRequest(string Operation, string? Pcm = null, string? Text = null,
    bool Russian = true);

internal sealed record IslandVoiceResponse(string Type, string? Text = null);

internal static class IslandMicrophoneLease
{
    internal static FileStream? TryAcquire(string? directory = null)
    {
        directory ??= IslandPreferences.Root;
        Directory.CreateDirectory(directory);
        try
        {
            return new FileStream(Path.Combine(directory, "microphone.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
        {
            return null;
        }
    }
}
