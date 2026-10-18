using System.Text.RegularExpressions;

namespace AIHub.Services;

/// <summary>Exact native contracts, independent from the recommended set or a running process.</summary>
public static partial class NativeRuntimeReleasePolicy
{
    public const string LegacySet = "001";
    public const string UpdatedSet = "002";
    public const string LegacyLlamaRevision = "d4c8e2c29ce2fb9a251a0a4a16d6c857b4f70f8c";
    public const string UpdatedLlamaRevision = "620d2225cc436ab0b001e3200e590e3b286ef9ab";
    public const string LegacySdRevision = "3f8527a46c54ecf4cb4ed6003da8e8982283c73c";
    public const string UpdatedSdRevision = "f0aac524d3f29140e60ba78f3cfb16d6acea87db";

    public static string ValidateSetId(string setId) => setId is LegacySet or UpdatedSet
        ? setId : throw new ArgumentException("Unknown native engine set.", nameof(setId));
    public static string LlamaRevision(string setId) => ValidateSetId(setId) == UpdatedSet ? UpdatedLlamaRevision : LegacyLlamaRevision;
    public static string SdRevision(string setId) => ValidateSetId(setId) == UpdatedSet ? UpdatedSdRevision : LegacySdRevision;
    public static string LlamaVersion(string setId) => ValidateSetId(setId) == UpdatedSet ? "b11669-lopata1" : "b9442-lopata1";
    public static string SdVersion(string setId) => ValidateSetId(setId) == UpdatedSet ? "f0aac52-lopata1" : "3f8527a-lopata1";
    [GeneratedRegex(@"\bversion:\s*9442\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LegacyLlamaVersion();
    [GeneratedRegex(@"\bversion:\s*0\.7\.0(?:-dev)?\s*\(build\s+11669\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UpdatedLlamaVersion();
    public static bool IsLlamaVersion(string output, string setId) => ValidateSetId(setId) == UpdatedSet
        ? UpdatedLlamaVersion().IsMatch(output) && output.Contains("620d2225c", StringComparison.OrdinalIgnoreCase)
        : LegacyLlamaVersion().IsMatch(output) && output.Contains("d4c8e2c", StringComparison.OrdinalIgnoreCase);
    public static bool IsSdVersion(string output, string setId) => output.Contains(SdRevision(setId)[..7], StringComparison.OrdinalIgnoreCase);
    public static string ManifestDigest(string family, string backend, string engineSetId = UpdatedSet) => (ValidateSetId(engineSetId), family, backend) switch
    {
        (LegacySet, "sd", "cuda12") => "7f2b1c8ad3f9c8f3f6b88deca38ca01b4b5bf461234f96e5546567a773a45dac",
        (UpdatedSet, "llama", "cpu") => "e1e4828863f4dea63c480ed24a109e109df96016ca935088b0bf93da9b84e3c5",
        (UpdatedSet, "llama", "vulkan") => "b6f1a8b7b5a7d614fd5aa9e922c324992f78572a8dcf008d607c1160aed57763",
        (UpdatedSet, "sd", "cpu") => "8b0a19ac4a72d6ba79b90e5033d79be196155cbba3a8b5bbc5e4475f592b3b17",
        (UpdatedSet, "sd", "vulkan") => "52331c89324bafaaacf5e4c59a0ceabd3bbeb872769184c8b3bbbad7ff4b6ffd",
        (UpdatedSet, "sd", "cuda12") => "c756c447b428fb833bdc414cdedbcfc0fb0ddbd2c1a6047263363fb61dc79850",
        _ => throw new ArgumentException("Unknown native runtime family/backend.")
    };
}
