using System.IO;
using System.Security.Cryptography;

namespace AIHub.Services.MusicLora;

/// <summary>The original engine pack stays immutable; this opt-in wrapper carries verified adapters through both Studio stages.</summary>
internal static class MusicLoraStudioRuntime
{
    public const string Revision = "v3.6.0-lopata-lora-2";
    public const string ServerHash = "872ee1d1201873773ddec2f83b5dc80fc80bc9179136a3be3cc739331675d851";
    public static async Task<string> ExecutableAsync(CancellationToken token)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "MusicLoraStudioRuntime", "music-server.exe");
        await using var file = File.OpenRead(executable);
        if (!Convert.ToHexString(await SHA256.HashDataAsync(file, token)).Equals(ServerHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Music.Lora.RuntimeInvalid");
        return executable;
    }
}
