using System.IO;
using System.Reflection;

namespace AIHub.Services;

/// <summary>Records only completed user outputs. Test hosts never write to the user's collection.</summary>
internal static class CollectionExportJournal
{
    internal static bool Record(string path, string scenario, string? contentSha256 = null)
    {
        var testRoot = Environment.GetEnvironmentVariable("LOPATA_COLLECTION_TEST_ROOT");
        if (string.IsNullOrWhiteSpace(testRoot)
            && Assembly.GetEntryAssembly()?.GetName().Name != typeof(CollectionExportJournal).Assembly.GetName().Name)
            return false;
        var directory = string.IsNullOrWhiteSpace(testRoot)
            ? Path.Combine(AppDataPaths.BaseDirectory, "Collection") : testRoot;
        var recorded = Lopata.Collection.CollectionJournal.RecordCompleted(path, scenario, directory, contentSha256);
        if (!recorded)
            OwnedProcessRegistry.Log("collection_output_not_recorded", "Collection");
        return recorded;
    }
}
