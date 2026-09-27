using System.IO;

namespace AIHub.Services;

public static class AppDataPaths
{
    private const string AppFolderName = "AI_HUB";

    public static string BaseDirectory { get; } =
#if UPDATE_STAND
        Path.GetFullPath(Environment.GetEnvironmentVariable("LOPATA_UPDATE_STAND_ROOT")
            ?? throw new InvalidOperationException("An isolated update stand root is required by this test build."));
#else
        Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppFolderName);
#endif

    public static string StatePath { get; } = Path.Combine(BaseDirectory, "state.json");

    public static string ComputerPassportPath { get; } = Path.Combine(BaseDirectory, "computer-passport.json");

    public static string StorageSettingsPath { get; } = Path.Combine(BaseDirectory, "storage-settings.json");

    public static string SettingsPath { get; } = Path.Combine(BaseDirectory, "settings.json");

    public static string UserProfilePath { get; } = Path.Combine(BaseDirectory, "user-profile.json");

    public static string LocalizationDirectory { get; } = Path.Combine(BaseDirectory, "Localization");

    public static string ManagedModelLibraryDirectory { get; } = Path.Combine(
        BaseDirectory,
        "ModelLibrary");

    public static string ManagedModelLibraryEntriesDirectory { get; } = Path.Combine(
        ManagedModelLibraryDirectory,
        "Entries");

    public static string ManagedModelLibraryEventsPath { get; } = Path.Combine(
        ManagedModelLibraryDirectory,
        "events.jsonl");

    public static string? ProjectRoot { get; } =
#if UPDATE_STAND
        null;
#else
        FindProjectRoot();
#endif

    public static string RuntimeDirectory { get; } = ProjectRoot is null
        ? Path.Combine(BaseDirectory, "Runtime")
        : Path.Combine(ProjectRoot, "Runtime");

    public static string BackendsDirectory { get; } = Path.Combine(RuntimeDirectory, "Backends");

    public static string ComponentsDirectory { get; } = Path.Combine(RuntimeDirectory, "Components");

    public static string ComponentRuntimesDirectory { get; } = Path.Combine(ComponentsDirectory, "Runtimes");

    public static string ComponentLibrariesDirectory { get; } = Path.Combine(ComponentsDirectory, "Libraries");

    public static string ComponentLanguagesDirectory { get; } = Path.Combine(ComponentsDirectory, "Languages");

    public static string ComponentModelsDirectory { get; } = Path.Combine(ComponentsDirectory, "Models");

    public static string ComponentViewersDirectory { get; } = Path.Combine(ComponentsDirectory, "Viewers");

    public static string ComponentManifestsDirectory { get; } = Path.Combine(ComponentsDirectory, "Manifests");

    public static string ComponentDownloadsDirectory { get; } = Path.Combine(ComponentsDirectory, "Downloads");

    public static string ComponentLogsDirectory { get; } = Path.Combine(ComponentsDirectory, "Logs");

    public static string ComponentStatePath { get; } = Path.Combine(ComponentManifestsDirectory, "component-state.json");

    public static string HuggingFaceCatalogDirectory { get; } = Path.Combine(
        RuntimeDirectory,
        "Каталоги",
        "HuggingFace");

    public static string HuggingFaceCatalogPath { get; } = Path.Combine(
        HuggingFaceCatalogDirectory,
        "catalog.json");

    public static string HuggingFaceCatalogSeedPath { get; } = ProjectRoot is not null
        ? Path.Combine(ProjectRoot, "Каталоги", "huggingface-catalog-seed.json")
        : Path.Combine(AppContext.BaseDirectory, "Catalogs", "huggingface-catalog-seed.json");

    public static void EnsureBaseDirectory()
    {
        Directory.CreateDirectory(BaseDirectory);
        Directory.CreateDirectory(LocalizationDirectory);
        Directory.CreateDirectory(ManagedModelLibraryEntriesDirectory);
        EnsureComponentDirectories();
    }

    public static void EnsureComponentDirectories()
    {
        Directory.CreateDirectory(ComponentsDirectory);
        Directory.CreateDirectory(ComponentRuntimesDirectory);
        Directory.CreateDirectory(ComponentLibrariesDirectory);
        Directory.CreateDirectory(ComponentLanguagesDirectory);
        Directory.CreateDirectory(ComponentModelsDirectory);
        Directory.CreateDirectory(ComponentViewersDirectory);
        Directory.CreateDirectory(ComponentManifestsDirectory);
        Directory.CreateDirectory(ComponentDownloadsDirectory);
        Directory.CreateDirectory(ComponentLogsDirectory);
    }

    private static string? FindProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "VERSION"))
                && Directory.Exists(Path.Combine(directory.FullName, "Runtime")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
