using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public sealed class LiteraryProjectStore(string indexPath)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static LiteraryProjectStore Default() => new(Path.Combine(AppDataPaths.BaseDirectory, "Literary", "projects.json"));

    public LiteraryProjectIndex Load()
    {
        if (!File.Exists(indexPath)) return new();
        var index = JsonSerializer.Deserialize<LiteraryProjectIndex>(File.ReadAllText(indexPath));
        if (index is null || index.SchemaVersion != 1 || index.Projects is null
            || index.Projects.Any(p => p is null || string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.ProjectPath))
            || index.Projects.Select(p => p.Id).Distinct().Count() != index.Projects.Count)
            throw new InvalidDataException("Invalid literary project index: " + indexPath);
        return index;
    }

    public void PruneMissing()
    {
        using var indexLock = AcquireLock();
        var index = Load();
        var missing = index.Projects.Where(p => ConfirmedMissing(p.ProjectPath)).ToArray();
        if (missing.Length > 0)
        {
            foreach (var entry in missing) index.Projects.Remove(entry);
            if (missing.Any(p => p.Id == index.ActiveId)) index.ActiveId = null;
            Save(index);
        }
    }

    private static bool ConfirmedMissing(string path)
    {
        try { _ = File.GetAttributes(path); return false; }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException)
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(path))!;
                return !root.StartsWith(@"\\") && new DriveInfo(root).IsReady;
            }
            catch { return false; }
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public static bool IsValidProjectName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || name != name.Trim()
            || name.EndsWith('.') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        var stem = name.Split('.')[0].ToUpperInvariant();
        return stem is not ("CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$")
            && !System.Text.RegularExpressions.Regex.IsMatch(stem, @"^(COM|LPT)[0-9¹²³]$");
    }

    public LiteraryProjectEntry Create(string parent, LiteraryProject project, IReadOnlyList<string> materials, Action<string>? initializeStaging = null)
    {
        if (!Path.IsPathFullyQualified(parent) || !Directory.Exists(parent)) throw new DirectoryNotFoundException(parent);
        if (!IsValidProjectName(project.ProjectName)) throw new ArgumentException("Invalid project folder name.");
        if (project.Genres.Count == 0 && string.IsNullOrWhiteSpace(project.CustomGenres)) throw new ArgumentException("A genre is required.");
        var destination = Path.Combine(Path.GetFullPath(parent), project.ProjectName);
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Already exists: " + destination);
        using var indexLock = AcquireLock();
        var index = Load(); // Do not create a project if its index is unreadable.
        if (index.Projects.Any(p => p.Id == project.Id)) throw new InvalidDataException("Duplicate project identifier.");
        var staging = Path.Combine(Path.GetFullPath(parent), ".lopata-new-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            project.Materials = [];
            if (project.BasedOnExistingWorld && materials.Count > 0)
            {
                Directory.CreateDirectory(Path.Combine(staging, "Materials"));
                for (var i = 0; i < materials.Count; i++)
                {
                    var relative = Path.Combine("Materials", $"{i + 1:D4}_" + Path.GetFileName(materials[i]));
                    File.Copy(materials[i], Path.Combine(staging, relative), false);
                    project.Materials.Add(relative);
                }
            }
            initializeStaging?.Invoke(staging);
            File.WriteAllText(Path.Combine(staging, "project.json"), JsonSerializer.Serialize(project, JsonOptions));
            var entry = new LiteraryProjectEntry(project.Id, project.ProjectName, destination);
            Directory.Move(staging, destination); // Never replace an existing project directory.
            index.Projects.Add(entry);
            try { Save(index); }
            catch
            {
                // Roll back only the folder just created by this operation.
                Directory.Move(destination, staging);
                throw;
            }
            return entry;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    public void SetActive(string id)
    {
        using var indexLock = AcquireLock();
        var index = Load();
        if (!index.Projects.Any(p => p.Id == id)) throw new ArgumentException("Unknown project.");
        index.ActiveId = id;
        Save(index);
    }

    public LiteraryProjectEntry CreateReserved(LiteraryProjectReservation reservation, LiteraryProject project,
        IReadOnlyList<string> materials, Action<string>? initialize)
    {
        var destination = reservation.Root;
        if (Path.GetFileName(destination) != project.ProjectName || !File.Exists(Path.Combine(destination, ".creation")))
            throw new IOException("Prepared project path changed.");
        using var indexLock = AcquireLock(); var index = Load();
        // Recover a crash between the project-file commit and registration, preserving identity.
        if (File.Exists(Path.Combine(destination, "project.json"))) project.Id = ReadProject(destination).Id;
        if (index.Projects.Any(p => p.Id == project.Id || string.Equals(p.ProjectPath, destination, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Project is already registered.");
        project.Materials = [];
        Directory.CreateDirectory(Path.Combine(destination, "Materials"));
        for (var i = 0; i < materials.Count; i++)
        {
            var relative = Path.Combine("Materials", $"{i + 1:D4}_" + Path.GetFileName(materials[i]));
            File.Copy(materials[i], Path.Combine(destination, relative), true); project.Materials.Add(relative);
        }
        initialize?.Invoke(destination);
        LiteraryChapterFiles.Write(Path.Combine(destination, "project.json"), JsonSerializer.Serialize(project, JsonOptions));
        var entry = new LiteraryProjectEntry(project.Id, project.ProjectName, destination);
        index.Projects.Add(entry);
        try
        {
            var layout = new LiteraryProjectLayout(destination); layout.Initialize(); layout.CommitLayout();
            Save(index);
        }
        catch { File.Delete(Path.Combine(destination, "project.json")); throw; }
        // Registration is committed. A failed marker cleanup must not make the caller roll back the index.
        try { File.Delete(Path.Combine(destination, ".creation")); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return entry;
    }

    public void Remove(LiteraryProjectEntry expected, LiteraryProjectRemoval mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        using var indexLock = AcquireLock();
        var index = Load();
        var entry = index.Projects.SingleOrDefault(p => p.Id == expected.Id)
            ?? throw new IOException("Project is no longer registered.");
        if (!string.Equals(entry.ProjectPath, expected.ProjectPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Project location has changed. Reopen the project list.");
        if (mode == LiteraryProjectRemoval.DeleteFiles)
            LiteraryProjectDeletion.Delete(entry, index.Projects, indexPath);
        index.Projects.Remove(entry);
        if (index.ActiveId == entry.Id) index.ActiveId = null;
        Save(index); // On failure the old index remains available for retry.
    }

    public static LiteraryProject ReadProject(string directory) =>
        JsonSerializer.Deserialize<LiteraryProject>(File.ReadAllText(Path.Combine(directory, "project.json")))
        is { SchemaVersion: 1 } project ? project : throw new InvalidDataException("Unsupported literary project.");

    private FileStream AcquireLock()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(indexPath))!);
        return new FileStream(indexPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private void Save(LiteraryProjectIndex index)
    {
        var temp = indexPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(index, JsonOptions));
            File.Move(temp, indexPath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
