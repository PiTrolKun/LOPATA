using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace AIHub.Services;

/// <summary>Locally registered experimental GGUF; no downloader or managed-model substitution.</summary>
public static class LiteraryModelLocation
{
    public const long SizeBytes = 7477218976;
    public const string Sha256 = "75b3107399b2f813b7ae0b98758e65aa5694b4de38b7576636e661c98506a565";
    public static string ConfigurationPath => Path.Combine(AppDataPaths.BaseDirectory, "literary-model.json");

    public static async Task<string> ResolveAsync(CancellationToken token)
    {
        if (!File.Exists(ConfigurationPath)) throw new FileNotFoundException("Register the verified Runeweaver GGUF in " + ConfigurationPath);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(ConfigurationPath, token));
        var path = json.RootElement.GetProperty("modelPath").GetString();
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path) || new FileInfo(path).Length != SizeBytes)
            throw new FileNotFoundException("Registered Runeweaver file is missing or has an unexpected size.");
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(file, token);
        if (!Convert.ToHexString(hash).Equals(Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Runeweaver SHA256 mismatch.");
        return path;
    }
}
