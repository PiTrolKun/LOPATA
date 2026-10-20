using System.IO;

namespace AIHub.Services;

public sealed record SystemCliDiscoveryResult(
    string CapabilityId,
    string CommandName,
    string ExecutablePath);

public sealed class SystemCliDiscoveryService
{
    private static readonly IReadOnlyDictionary<string, string[]> CapabilityCommands =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["read.audio"] = ["ffmpeg.exe", "ffmpeg"],
            ["read.video"] = ["ffmpeg.exe", "ffmpeg"],
            ["extract.video_frames"] = ["ffmpeg.exe", "ffmpeg"],
            ["edit.audio"] = ["ffmpeg.exe", "ffmpeg"],
            ["edit.video"] = ["ffmpeg.exe", "ffmpeg"],
            ["read.image_extended"] = ["magick.exe", "magick"],
            ["edit.image"] = ["magick.exe", "magick"],
            ["convert.image"] = ["magick.exe", "magick"],
            ["extract.image_ocr"] = ["tesseract.exe", "tesseract"],
            ["convert.legacy_office"] = ["soffice.exe", "soffice"],
            ["extract.audio_transcript"] = ["whisper-cli.exe", "whisper-cli"]
        };

    public SystemCliDiscoveryResult? Find(string capabilityId)
    {
        if (capabilityId == "convert.legacy_office" && Books.BookConverterRuntime.FindBundledLibreOffice() is { } bundledOffice)
            return new SystemCliDiscoveryResult(capabilityId, "soffice.com", bundledOffice);
        if (!CapabilityCommands.TryGetValue(capabilityId, out var commands))
        {
            return null;
        }

        foreach (var command in commands)
        {
            var path = FindOnPath(command);
            if (!string.IsNullOrWhiteSpace(path))
            {
                return new SystemCliDiscoveryResult(capabilityId, command, path);
            }
        }

        return null;
    }

    private static string FindOnPath(string command)
    {
        if (Path.IsPathFullyQualified(command) && File.Exists(command))
        {
            return Path.GetFullPath(command);
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathValue.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, command);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch (ArgumentException)
            {
                // Ignore malformed PATH entries and continue discovery.
            }
        }

        return string.Empty;
    }
}
