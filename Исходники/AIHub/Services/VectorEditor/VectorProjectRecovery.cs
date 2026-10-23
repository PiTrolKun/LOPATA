using System.IO;

namespace AIHub.Services.VectorEditor;

public static class VectorProjectRecovery
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string,object> Gates=new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string,DateTimeOffset> Versions=new();
    public static string Root => Path.Combine(AppDataPaths.BaseDirectory, "VectorGraphics", "EditorRecovery");
    public static string PathFor(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Invalid project identity.");
        return Path.Combine(Root, id + ".lopataSVG");
    }
    public static void Save(VectorProject project)
    {
        lock(Gates.GetOrAdd(project.Id,_=>new object())){
            if(Versions.TryGetValue(project.Id,out var previous)&&previous>project.ModifiedUtc)return;
            VectorProjectArchive.Save(project,PathFor(project.Id));Versions[project.Id]=project.ModifiedUtc;
        }
    }
    public static string[] Available() => Directory.Exists(Root) ? Directory.GetFiles(Root, "*.lopataSVG").OrderByDescending(File.GetLastWriteTimeUtc).ToArray() : [];
    public static void Remove(string id)
    {
        lock(Gates.GetOrAdd(id,_=>new object())){
            var path = PathFor(id); Lopata.Updates.SafeUpdatePath.RejectLinks(path); if (File.Exists(path)) File.Delete(path);
            Versions[id]=DateTimeOffset.UtcNow;
        }
    }
}
