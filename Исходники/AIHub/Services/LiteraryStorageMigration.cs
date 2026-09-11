using System.IO;
using System.Text.Json;

namespace AIHub.Services;

public static class LiteraryStorageMigration
{
    public static async Task MigrateAsync(string directory, IProgress<LiteraryPreparationProgress> progress, CancellationToken ct)
    {
        var layout = new LiteraryProjectLayout(directory);
        if (layout.Migrated) { layout.Initialize(); return; }
        // Persistent backup and all intermediate artifacts belong to this same project.
        var backup = Path.Combine(directory, "MigrationBackup-v1");
        if (!File.Exists(Path.Combine(backup, ".complete")))
        {
            Directory.CreateDirectory(backup);
            foreach (var name in new[] { "project.json", "chapters", "Materials", "Rag", "working-draft.txt" })
            {
                var source = Path.Combine(directory, name); var target = Path.Combine(backup, name);
                if (Directory.Exists(source)) CopyTree(source, target);
                else if (File.Exists(source) && !File.Exists(target)) File.Copy(source, target);
            }
            LiteraryChapterFiles.Write(Path.Combine(backup, ".complete"), "Original project files copied before storage migration.");
        }
        layout.Initialize();
        var manifestPath = Path.Combine(layout.Rag, "Source", "manifest.json");
        if (File.Exists(manifestPath))
        {
            var manifest = JsonSerializer.Deserialize<LiterarySourceIndex.Manifest>(File.ReadAllText(manifestPath))
                ?? throw new InvalidDataException("Invalid source manifest.");
            if (manifest.Dimension != 1024 || manifest.ModelRevision != GigaEmbeddingInstallation.Revision || manifest.Kind != "reference")
                throw new InvalidDataException("Unsupported source index.");
            var runtime = layout.CreateRuntime();
            try
            {
                layout.EnsurePresent();
                var count = await LiteraryRagImport.ReplaceAsync(runtime, manifest.Id,
                    Path.Combine(layout.Rag, "Source", "vectors.jsonl"), progress, ct);
                if (count != manifest.Points) throw new InvalidDataException("Migrated point count mismatch.");
            }
            finally { await runtime.StopAsync(); }
            // Retire the external collection only after the project-owned copy was verified.
            await QdrantRuntime.Shared.DeleteLiteraryIndexAsync(manifest.Id, ct);
            await QdrantRuntime.Shared.StopAsync();
        }
        layout.CommitLayout();
        progress.Report(new("Ready", 100, ""));
    }
    private static void CopyTree(string source, string target)
    {
        LiteraryProjectLayout.CheckTreePath(source); Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked project file.");
            var destination = Path.Combine(target, Path.GetFileName(file));
            if (!File.Exists(destination)) File.Copy(file, destination, false);
        }
        foreach (var folder in Directory.EnumerateDirectories(source)) CopyTree(folder, Path.Combine(target, Path.GetFileName(folder)));
    }
}
