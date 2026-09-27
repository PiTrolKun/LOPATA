using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lopata.Updates;

public sealed record InstalledUpdateState(string AppDirectory, string LlamaDirectory, string ChatLlmDirectory)
{
    public static string UserDataDirectory =>
#if UPDATE_STAND
        Path.GetFullPath(Environment.GetEnvironmentVariable("LOPATA_UPDATE_STAND_ROOT")
            ?? throw new InvalidOperationException("An isolated update stand root is required by this test build."));
#else
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AI_HUB");
#endif
    public static string RegistrationPath => Path.Combine(UserDataDirectory, "Updates", "installation.json");
    public static string LauncherPath => Path.Combine(UserDataDirectory, "UpdateHost", "LOPATA.Updater.exe");
    [JsonIgnore] public string StateDirectory => Path.Combine(UserDataDirectory, "Updates", "Installations",
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(AppDirectory).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())))[..24]);
    [JsonIgnore] public string CacheDirectory => Path.Combine(StateDirectory, "packages");
    public string StageDirectory(string version) => SafeUpdatePath.Resolve(StateDirectory, "staging/" + UpdateManifest.NumericVersion(version));
    public UpdateRoots Roots() => new(new Dictionary<string, string>
        { ["app"] = AppDirectory, ["llama"] = LlamaDirectory, ["chatllm"] = ChatLlmDirectory });

    public static InstalledUpdateState? Read()
    {
        SafeUpdatePath.RejectLinks(RegistrationPath);
        if (!File.Exists(RegistrationPath)) return null;
        var state = JsonSerializer.Deserialize<InstalledUpdateState>(File.ReadAllText(RegistrationPath), UpdateManifest.JsonOptions)
            ?? throw new InvalidDataException("Missing installation registration.");
        _ = state.Roots();
        return state;
    }

    public void Save()
    {
        _ = Roots();
        DurableUpdateFiles.WriteJson(RegistrationPath, this);
    }
}

public static class UpdateChannelStore
{
    public static string PathIn(string updatesDirectory) => Path.Combine(updatesDirectory, "channel.json");
    public static UpdateDelivery? Read(string updatesDirectory)
    {
        var path = PathIn(updatesDirectory);
        SafeUpdatePath.RejectLinks(path);
        if (!File.Exists(path)) return null;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("channel").GetString() switch
        {
            "stable" => UpdateDelivery.FullInstaller,
            "beta" => UpdateDelivery.FilePatch,
            _ => throw new InvalidDataException("Unknown update direction.")
        };
    }

    public static void Save(string updatesDirectory, UpdateDelivery delivery)
    {
        if (!Enum.IsDefined(delivery)) throw new InvalidDataException("Invalid update direction.");
        DurableUpdateFiles.WriteJson(PathIn(updatesDirectory), new { schemaVersion = 1,
            channel = delivery == UpdateDelivery.FullInstaller ? "stable" : "beta" });
    }
}

public sealed record PreparedApplicationUpdate(string Version, UpdateDelivery Delivery, InstallerArtifact? Installer,
    string? InstallerPath, SignedManifest? Files, bool ApplyOnNextLaunch, UpdateNote[]? Notes = null, bool NotesIncomplete = false)
{
    public void Validate(IReadOnlyDictionary<string, string> keys)
    {
        _ = UpdateManifest.NumericVersion(Version);
        if (Delivery == UpdateDelivery.FilePatch)
        {
            if (Files is null || Files.Verify(keys).Version != Version || Installer is not null || InstallerPath is not null)
                throw new InvalidDataException("Invalid prepared file update.");
        }
        else if (Delivery == UpdateDelivery.FullInstaller)
        {
            if (Installer is null || InstallerPath is null || Files is not null || Installer.Size <= 0
                || Installer.FileName != $"LOPATA_Setup_{Version}.exe" || !Path.IsPathFullyQualified(InstallerPath)
                || Path.GetFileName(InstallerPath) != Installer.FileName
                || (!PublishedUpdateCatalog.IsAssetUrl(Installer.Url, "v" + Version, Installer.FileName)
                    && !PublishedUpdateCatalog.IsAssetUrl(Installer.Url, Version, Installer.FileName)))
                throw new InvalidDataException("Invalid prepared installer.");
            UpdateManifest.ValidateHash(Installer.Sha256);
        }
        else throw new InvalidDataException("Invalid prepared update direction.");
    }
}

public sealed class PreparedUpdateStore(string stateDirectory, IReadOnlyDictionary<string, string> keys)
{
    private string PendingPath => Path.Combine(stateDirectory, "prepared.json");

    public PreparedApplicationUpdate? Read()
    {
        SafeUpdatePath.RejectLinks(PendingPath);
        if (!File.Exists(PendingPath)) return null;
        if (new FileInfo(PendingPath).Length > SignedManifest.MaximumEnvelopeBytes + 65536)
            throw new InvalidDataException("Oversized prepared update record.");
        var prepared = JsonSerializer.Deserialize<PreparedApplicationUpdate>(File.ReadAllBytes(PendingPath), UpdateManifest.JsonOptions)
            ?? throw new InvalidDataException("Empty prepared update record.");
        prepared.Validate(keys);
        return prepared;
    }

    public void Save(PreparedApplicationUpdate prepared)
    {
        prepared.Validate(keys);
        DurableUpdateFiles.WriteJson(PendingPath, prepared);
    }

    public void Clear()
    {
        SafeUpdatePath.RejectLinks(PendingPath);
        File.Delete(PendingPath);
    }
}
