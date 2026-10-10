using System.IO;

namespace AIHub.Models;

public sealed record AudioShellRequest(string RequestId, IReadOnlyList<string> Paths)
{
    public AudioShellRequest Validate()
    {
        // Share the strict literal-path guard, not the image operation or its executor.
        var guarded = new ImageShellRequest(RequestId, ImageShellOperation.WebP, Paths).Validate();
        if (guarded.Paths.Any(p => !Supported(p))) throw new InvalidDataException("AudioShell.UnsupportedFile");
        return this with { RequestId = guarded.RequestId, Paths = guarded.Paths };
    }
    public static bool Supported(string path) => Path.GetExtension(path).ToLowerInvariant() is ".mp3" or ".opus" or ".flac" or ".wav";
    public static AudioShellRequest? ParseArguments(IReadOnlyList<string> args)
    {
        var index = args.ToList().IndexOf("--shell-audio");
        if (index < 0) return null;
        if (index != 0 || args.Count < 3 || args[1] != "--") throw new InvalidDataException("AudioShell.InvalidRequest");
        return new AudioShellRequest(Guid.NewGuid().ToString("N"), args.Skip(2).ToArray()).Validate();
    }
}
