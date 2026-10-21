using System.IO;

namespace AIHub.Services;

public static class MusicStemFolder
{
    public static async Task CopyAsync(string source, string destinationParent, CancellationToken token)
    {
        source = Path.GetFullPath(source); destinationParent = Path.GetFullPath(destinationParent);
        var target = Path.Combine(destinationParent, Path.GetFileName(source));
        if (target.Equals(source, StringComparison.OrdinalIgnoreCase) && Directory.Exists(source)) return;
        if (Directory.Exists(target) || File.Exists(target) || destinationParent.Equals(source, StringComparison.OrdinalIgnoreCase)
            || destinationParent.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("A song folder already exists or the destination is inside the source.");
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0 || Directory.GetDirectories(source).Length != 0)
            throw new IOException("Unexpected linked or nested song bundle.");
        var files = Directory.GetFiles(source);
        if (files.Any(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0 || p.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("The song bundle is not ready for copying.");
        Directory.CreateDirectory(target);
        foreach (var path in files) {
            await using var input = File.OpenRead(path);
            await using var output = new FileStream(Path.Combine(target, Path.GetFileName(path)), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await input.CopyToAsync(output, token); await output.FlushAsync(token);
        }
    }
}
