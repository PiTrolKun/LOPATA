using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Pinned headless Studio; models remain in the ordinary managed library.</summary>
public static class MusicStudioRuntime
{
    public const string Variation = "music-yue2-studio-q8";
    public const string ComponentId = "runtime.music-yue2-studio";
    public const string CurrentComponentId = "runtime.music-yue2-studio-002";
    public const string CompanionId = "music-yue2-studio-companion";
    public static readonly MusicStudioProfile Legacy = new("001", "v3.4.0-lopata-paths-1",
        "9125be3cf9ba720ac439a81cd09ff8bcc2a00368", "1141479c725b803a3649c900fd3e6fcb27f6f595", "studio-win-x64", false);
    public static readonly MusicStudioProfile Current = new("002", "v3.6.0-lopata-paths-1",
        "c3e214f3a65f3f56c18e107127ebd2ebad5150ad", "c8516dbf44d660cca40d3f420c67063e37ec8258", "studio-3.6.0-win-x64", true);
    public static MusicStudioProfile SelectedProfile => EngineSetSelection.ActiveId switch {
        "001" => Legacy, "002" => Current, _ => throw new InvalidDataException("Unsupported Studio engine set.") };
    public static string Revision => SelectedProfile.Revision;
    public static string SourceRevision => SelectedProfile.SourceRevision;
    public static string EngineRevision => SelectedProfile.EngineRevision;
    public const string CompanionRevision = "e2e63d859f3af879baf1b4d4e9f22d1eeda6fde5";
    public static string Pack => SelectedProfile.Pack;
    public static string DirectoryPath => SelectedProfile.DirectoryPath;
    internal static string ProfileDirectory(MusicStudioProfile profile) => EngineSetSelection.ResolveDirectory(profile.EngineSetId, "studio", "native");
    public static MusicStudioProfile ResolveProfile(string revision, string pack) => new[] { Legacy, Current }
        .SingleOrDefault(p => p.Revision == revision && p.Pack == pack)
        ?? throw new InvalidDataException("Unsupported Studio runtime contract.");
    public static bool IsSupported(string revision, string pack) => new[] { Legacy, Current }.Any(p => p.Revision == revision && p.Pack == pack);
    public static bool Available => File.Exists(Path.Combine(DirectoryPath, "manifest.json"));
    public static IReadOnlyList<ManagedModelArtifactCard> Cards(string root) => [.. MusicComponentCatalog.CreateCards(root), Companion(root)];
    private static ManagedModelArtifactCard Companion(string root) => new() {
        ModelArtifactId = CompanionId, DisplayName = "Studio · Decoder companion v9", Family = "YuE2",
        Role = ManagedModelRoles.Tool, Provider = "Hugging Face",
        RepositoryId = "Mothersuperior/yue2-mothersuperior-realaudio-tokenizer-v4", Revision = CompanionRevision,
        Format = "Safetensors", License = "CC BY-NC 4.0", RuntimeBackend = "YuE2 Studio / yue2.cpp",
        SourcePage = "https://huggingface.co/Mothersuperior/yue2-mothersuperior-realaudio-tokenizer-v4",
        IsManaged = true, CanRemoveFiles = true, ModelsRoot = root,
        InstallDirectory = string.IsNullOrWhiteSpace(root) ? "" : Path.Combine(root, "Music", CompanionId, CompanionRevision[..12]),
        Origin = ManagedModelOrigins.PredefinedScenario,
        Consumers = [new() { Id = ScenarioNavigationCatalog.Music, DisplayName = "Музыка / Music", Kind = "scenario" }],
        Files = [new() { RelativePath = "nar_lora_joint_v9.safetensors", SizeBytes = 140_560_592,
            Sha256 = "585f303da1d5252d228d1e8ac6d4c4d11d970df9297406935cc8bdafa49cfa7e",
            SourceUrl = "https://huggingface.co/Mothersuperior/yue2-mothersuperior-realaudio-tokenizer-v4/resolve/" + CompanionRevision + "/nar_lora_joint_v9.safetensors",
            Purpose = "audio_decoder_adapter" }]
    };
    public static Task VerifyAsync(CancellationToken token) => VerifyAsync(SelectedProfile, token);
    public static async Task VerifyAsync(MusicStudioProfile profile, CancellationToken token)
    {
        using var input = File.OpenRead(Path.Combine(profile.DirectoryPath, "manifest.json"));
        using var manifest = await JsonDocument.ParseAsync(input, cancellationToken: token);
        var root = manifest.RootElement;
        if (root.GetProperty("Revision").GetString() != profile.Revision || root.GetProperty("SourceRevision").GetString() != profile.SourceRevision
            || root.GetProperty("EngineRevision").GetString() != profile.EngineRevision)
            throw new InvalidDataException("Unsupported Studio runtime manifest.");
        var files = root.GetProperty("Files").EnumerateArray().ToArray();
        foreach (var required in new[] { "music-server.exe", "engine/yue-server.exe", "engine/ggml.dll", "engine/ggml-base.dll",
            "engine/ggml-cpu-x64.dll", "engine/ggml-vulkan.dll", "engine/cuda12/ggml-cuda.dll", "engine/cuda13/ggml-cuda.dll",
            "engine/cublas64_12.dll", "engine/cublasLt64_12.dll", "engine/cublas64_13.dll", "engine/cublasLt64_13.dll" })
            if (files.Count(f => f.GetProperty("Name").GetString() == required) != 1)
                throw new InvalidDataException("Incomplete Studio runtime: " + required);
        var directory = Path.GetFullPath(profile.DirectoryPath) + Path.DirectorySeparatorChar;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in files)
        {
            var name = entry.GetProperty("Name").GetString()!;
            var path = Path.GetFullPath(Path.Combine(directory, name));
            if (!path.StartsWith(directory, StringComparison.OrdinalIgnoreCase) || !names.Add(name))
                throw new InvalidDataException("Unsafe Studio manifest path.");
            using var file = File.OpenRead(path);
            if (file.Length != entry.GetProperty("Bytes").GetInt64() || !Convert.ToHexString(await SHA256.HashDataAsync(file, token))
                .Equals(entry.GetProperty("Sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Studio runtime checksum mismatch: " + name);
        }
    }
    public static async Task VerifyModelsAsync(string modelsRoot, CancellationToken token)
    {
        foreach (var card in Cards(modelsRoot)) foreach (var expected in card.Files)
        {
            using var file = File.OpenRead(Path.Combine(card.InstallDirectory, expected.RelativePath));
            if (file.Length != expected.SizeBytes || !Convert.ToHexString(await SHA256.HashDataAsync(file, token))
                .Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Studio model checksum mismatch: " + expected.RelativePath);
        }
    }
}
