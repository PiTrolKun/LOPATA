using System.IO;

namespace AIHub.Services;

internal static class MusicAudioScoreCache
{
    internal static async Task<string> GetAsync(MusicAudioInput input, bool harmony, Func<Task<string>> produce, CancellationToken token)
    {
        var root = Path.Combine(AppDataPaths.BaseDirectory, "Music", "ScoreCache", MusicCoverScoreCatalog.Revision[..12]); Directory.CreateDirectory(root);
        var path = Path.Combine(root, input.Sha256 + (harmony ? "-full.abc" : "-melody.abc"));
        var hash = path + ".sha256";
        if (File.Exists(path) && File.Exists(hash) && await File.ReadAllTextAsync(hash, token) == await MusicAudioTaskFiles.HashAsync(path, token))
            return await File.ReadAllTextAsync(path, token);
        var text = await produce(); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            await File.WriteAllTextAsync(temporary, text, token);
            var digest = await MusicAudioTaskFiles.HashAsync(temporary, token);
            File.Move(temporary, path, true);
            await File.WriteAllTextAsync(hash, digest, token); return text;
        } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
