using System.Text.Json;

namespace Lopata.Updates;

internal static class DurableUpdateFiles
{
    public static void WriteJson<T>(string path, T value)
    {
        SafeUpdatePath.RejectLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            65536, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, value, UpdateManifest.JsonOptions);
            stream.Flush(true);
        }
        SafeUpdatePath.RejectLinks(path);
        File.Move(temporary, path, true);
    }

    public static async Task CopyAsync(string source, string destination, CancellationToken token)
    {
        SafeUpdatePath.RejectLinks(source);
        SafeUpdatePath.RejectLinks(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await input.CopyToAsync(output, token);
        output.Flush(true);
    }
}

public enum UpdatePhase { Prepared, Applying, AwaitingHealth, Healthy, RollingBack, RolledBack }
public sealed record UpdateCheckpoint(string Point, int CompletedFiles, string? FileKey);
public sealed record UpdateJournal(string Id, UpdatePhase Phase, SignedManifest? Previous,
    SignedManifest Target, UpdateOperation[] Operations);
