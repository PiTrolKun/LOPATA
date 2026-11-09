using System.IO;
using System.Text.Json;
using AIHub.Models;
using Lopata.Collection;

namespace AIHub.Services;

internal sealed record CollectionConfigurationPaths(string ApplicationDirectory, string DataDirectory,
    string RuntimeDirectory, string? ProjectRoot)
{
    internal string CollectionDirectory => Path.Combine(DataDirectory, "Collection");
    internal static CollectionConfigurationPaths Current => new(AppContext.BaseDirectory,
        AppDataPaths.BaseDirectory, AppDataPaths.RuntimeDirectory, AppDataPaths.ProjectRoot);
}

/// <summary>One search/privacy boundary for normal startup and post-install initialization.</summary>
internal static class CollectionConfigurationFactory
{
    internal static CollectionConfig Create(AppSettings settings, StorageSettings storage,
        CollectionConfigurationPaths paths, bool enabled, string? workerPath = null)
    {
        var old = CollectionConfig.Load(paths.CollectionDirectory);
        var roots = storage.Results.Locations.Select(p => p.Path)
            .Concat([settings.MusicOutputFolder, settings.ImageGeneration.Folder, settings.ScreenCapture.Folder])
            .Concat(settings.Behavior.CollectionFolders)
            .Where(p => !string.IsNullOrWhiteSpace(p) && Path.IsPathFullyQualified(p))
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var excluded = storage.Models.Locations.Select(p => p.Path)
            .Concat([paths.ApplicationDirectory, paths.CollectionDirectory, paths.RuntimeDirectory])
            .Concat(new[]
            {
                "OfficeEditor/Drafts", "OfficeReader/Sessions", "OfficeReader/Speech/Temporary",
                "OfficeReader/Translation/Temporary", "VideoPreparation/Recovery", "VideoEditor/Projects",
                "VectorGraphics/EditorRecovery", "VectorGraphics/Jobs", "VideoPreparation/H3Jobs",
                "VideoPreparation/VaceJobs", "Chat/Audio"
            }.Select(relative => Path.Combine(paths.DataDirectory, relative)))
            .Concat(paths.ProjectRoot is { } projectRoot
                ? new[] { Path.Combine(projectRoot, "_tmp"), Path.Combine(projectRoot, "_backups") } : [])
            .Where(p => !string.IsNullOrWhiteSpace(p) && Path.IsPathFullyQualified(p))
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var searchRoots = roots.Concat(new[]
            {
                Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.MyPictures,
                Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.DesktopDirectory
            }.Select(Environment.GetFolderPath))
            .Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return old with
        {
            Enabled = enabled, Roots = roots, SearchRoots = searchRoots, ExcludedRoots = excluded,
            Language = settings.LanguageCode == "ru" ? "ru" : "en",
            ExpectedWorkerPath = workerPath ?? old.ExpectedWorkerPath,
            AuditEnabled = settings.Behavior.ComponentAuditEnabled
        };
    }

    internal static void SaveIfChanged(string directory, CollectionConfig configuration)
    {
        var current = CollectionConfig.Load(directory);
        if (JsonSerializer.Serialize(current, CollectionFiles.Json)
            != JsonSerializer.Serialize(configuration, CollectionFiles.Json)) configuration.Save(directory);
    }
}
