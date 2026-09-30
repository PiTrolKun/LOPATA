using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AIHub.Services;

internal static class BackgroundSpeechCache
{
    internal static string? PathFor(string text, string language, int volume, int rate)
    {
        var id = ApplicationBackgroundOperations.Current?.State?.Id;
        if (!Guid.TryParseExact(id, "N", out _)) return null;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{language}|{volume}|{rate}|{text}")));
        return Path.Combine(AppDataPaths.BaseDirectory, "Background", "Audio", id, key + ".wav");
    }

    internal static void Save(string source, string destination)
    {
        Lopata.Updates.SafeUpdatePath.RejectLinks(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".tmp";
        using (var input = File.OpenRead(source))
        using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { input.CopyTo(output); output.Flush(true); }
        File.Move(temporary, destination, true);
    }
}
