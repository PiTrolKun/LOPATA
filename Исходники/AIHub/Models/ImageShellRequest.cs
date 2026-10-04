using System.IO;

namespace AIHub.Models;

public enum ImageShellOperation { WebP, Upscale2 }

/// <summary>A literal file request; it never contains a command line to execute.</summary>
public sealed record ImageShellRequest(string RequestId, ImageShellOperation Operation, IReadOnlyList<string> Paths)
{
    public const int MaximumPaths = 128;
    public const int MaximumPathLength = 32760;

    public ImageShellRequest Validate()
    {
        if (!Guid.TryParseExact(RequestId, "N", out var id) || id == Guid.Empty
            || !Enum.IsDefined(Operation) || Paths is null || Paths.Count is < 1 or > MaximumPaths)
            throw new InvalidDataException("Invalid image shell request.");
        var normalized = new List<string>();
        foreach (var path in Paths)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length > MaximumPathLength
                || path.Any(char.IsControl) || path.IndexOfAny(['"', '<', '>', '|', '*', '?']) >= 0
                || !Path.IsPathFullyQualified(path)
                || path.StartsWith(@"\\.\", StringComparison.Ordinal)
                || path.StartsWith(@"\\?\", StringComparison.Ordinal)
                || path.IndexOf(':', 2) >= 0)
                throw new InvalidDataException("Shell image paths must be ordinary absolute file paths.");
            normalized.Add(Path.GetFullPath(path));
        }
        return this with { RequestId = id.ToString("N"), Paths = normalized.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
    }

    /// <summary>Returns null for ordinary launches; malformed shell launches always fail closed.</summary>
    public static ImageShellRequest? ParseArguments(IReadOnlyList<string> arguments)
    {
        var shellIndex = -1;
        for (var i = 0; i < arguments.Count; i++)
            if (arguments[i] == "--shell-image") { shellIndex = i; break; }
        if (shellIndex < 0) return null;
        // Only known startup flags may precede the exact Explorer verb syntax.
        for (var i = 0; i < shellIndex; i++)
        {
            if (arguments[i] is "--background" or "--launched-by-updater") continue;
            if (arguments[i] == "--update-health" && i + 1 < shellIndex
                && Guid.TryParseExact(arguments[++i], "N", out _)) continue;
            throw new InvalidDataException("Unexpected shell image startup argument.");
        }
        if (arguments.Count < shellIndex + 4 || arguments[shellIndex + 2] != "--")
            throw new InvalidDataException("Expected --shell-image webp|upscale2 -- <files>.");
        var operation = arguments[shellIndex + 1] switch
        {
            "webp" => ImageShellOperation.WebP,
            "upscale2" => ImageShellOperation.Upscale2,
            _ => throw new InvalidDataException("Unknown shell image operation.")
        };
        return new ImageShellRequest(Guid.NewGuid().ToString("N"), operation,
            arguments.Skip(shellIndex + 3).ToArray()).Validate();
    }
}
