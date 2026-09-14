using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace AIHub.Services;

/// <summary>The same verified Gamma weights, used without its vision projector.</summary>
public static class LiteraryModelLocation
{
    public const long SizeBytes = 12599187008;
    public const string Sha256 = "95580dbdaad579582ee898257116abc18d7f3625a00c16a15735d41444a09f5e";
    public const string FileName = "Qwen3.8-27B-Ridge-3.7bpw.gguf";
    public const string DisplayName = "Qwen3.8-27B-Ridge 3.7 bpw";
    public static string ConfigurationPath => Path.Combine(AppDataPaths.BaseDirectory, "literary-model.json");
    public static string DownloadPath
    {
        get
        {
            var root = new StorageSettingsStore().LoadOrCreate().Models.Locations.FirstOrDefault()?.Path;
            var card = ManagedModelCatalog.CreateOmniGamma(string.IsNullOrWhiteSpace(root) ? AppDataPaths.ComponentModelsDirectory : root);
            return Path.Combine(card.InstallDirectory, FileName);
        }
    }
    public static string DownloadUrl => $"https://huggingface.co/{ManagedModelCatalog.OmniGammaRepository}/resolve/{ManagedModelCatalog.OmniGammaRevision}/{FileName}";

    public static async Task<string> ResolveAsync(CancellationToken token)
    {
        var candidates = new List<string>();
        var installed = new ManagedModelLibraryStore().Load(ManagedModelCatalog.OmniGammaArtifactId);
        if (!string.IsNullOrWhiteSpace(installed?.InstallDirectory)) candidates.Add(Path.Combine(installed.InstallDirectory, FileName));
        candidates.Add(DownloadPath);
        if (File.Exists(ConfigurationPath))
        {
            try
            {
                using var json = JsonDocument.Parse(await File.ReadAllTextAsync(ConfigurationPath, token));
                if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("modelPath", out var value)
                    && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } registered)
                    candidates.Add(registered);
            }
            catch (JsonException) { /* An obsolete registration must not hide the managed model. */ }
        }
        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path) || new FileInfo(path).Length != SizeBytes) continue;
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await SHA256.HashDataAsync(file, token);
            if (!Convert.ToHexString(hash).Equals(Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Qwen literary GGUF SHA256 mismatch.");
            return path;
        }
        throw new FileNotFoundException("Prepare the verified Qwen literary GGUF through scenario setup.");
    }
}
