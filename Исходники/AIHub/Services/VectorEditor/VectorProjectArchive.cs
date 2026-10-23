using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AIHub.Services.VectorGraphics;

namespace AIHub.Services.VectorEditor;

/// <summary>Versioned ZIP container. Entries are read in place, never extracted into the filesystem.</summary>
public static class VectorProjectArchive
{
    public sealed record Summary(string Name,DateTimeOffset ModifiedUtc);
    private const long MaxBytes = 512L * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private sealed record Entry(string Id, string Name, string File, string? JobId = null, double Zoom = 1, double PanX = 0, double PanY = 0);
    private sealed record Manifest(int Schema, string Id, string Name, DateTimeOffset CreatedUtc, DateTimeOffset ModifiedUtc,
        string ActiveSheetId, Entry[] Sheets, Entry[] Objects, OutputProvenance Provenance);
    public static Summary Describe(string source)
    {
        Lopata.Updates.SafeUpdatePath.RejectLinks(Path.GetFullPath(source));
        using var file=File.OpenRead(source);using var zip=new ZipArchive(file,ZipArchiveMode.Read,false,Utf8);
        var manifest=JsonSerializer.Deserialize<Manifest>(Read(zip,"project.json",4*1024*1024))??throw new InvalidDataException("Vector.Editor.Error.Archive");
        if(manifest.Schema!=1||manifest.Name is null||manifest.Name.Length>1024)throw new InvalidDataException("Vector.Editor.Error.Archive");
        return new(manifest.Name,manifest.ModifiedUtc);
    }

    public static void Save(VectorProject project, string destination)
    {
        Validate(project);
        var path = Path.GetFullPath(destination); Lopata.Updates.SafeUpdatePath.RejectLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                using (var zip = new ZipArchive(file, ZipArchiveMode.Create, true, Utf8))
                {
                    var sheets = project.Sheets.Select(s => new Entry(s.Id, s.Name, "sheets/" + s.Id + ".svg", Zoom: s.Zoom, PanX: s.PanX, PanY: s.PanY)).ToArray();
                    var objects = project.Objects.Select(s => new Entry(s.Id, s.Name, "objects/" + s.Id + ".svg", s.JobId)).ToArray();
                    var manifest = new Manifest(1, project.Id, project.Name, project.CreatedUtc, DateTimeOffset.UtcNow,
                        project.ActiveSheetId, sheets, objects, VectorEditorSvg.Facts(project.Sheets.Select(s => s.Svg).Concat(project.Objects.Select(s => s.Svg)), project.CreatedUtc, "vector-editor/project"));
                    Write(zip, "project.json", JsonSerializer.Serialize(manifest));
                    for (var i = 0; i < sheets.Length; i++) Write(zip, sheets[i].File, project.Sheets[i].Svg);
                    for (var i = 0; i < objects.Length; i++) Write(zip, objects[i].File, project.Objects[i].Svg);
                }
                file.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static VectorProject Load(string source)
    {
        Lopata.Updates.SafeUpdatePath.RejectLinks(Path.GetFullPath(source));
        using var file = File.OpenRead(source); using var zip = new ZipArchive(file, ZipArchiveMode.Read, false, Utf8);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
        foreach (var entry in zip.Entries)
        {
            if (!names.Add(entry.FullName) || entry.FullName.Contains('\\') || entry.FullName.StartsWith('/')
                || entry.FullName.Split('/').Any(p => p is "" or "." or "..") || entry.Length < 0 || entry.Length > MaxBytes
                || (total = checked(total + entry.Length)) > MaxBytes) throw new InvalidDataException("Vector.Editor.Error.Archive");
        }
        var manifest = JsonSerializer.Deserialize<Manifest>(Read(zip, "project.json", 4 * 1024 * 1024))
            ?? throw new InvalidDataException("Vector.Editor.Error.Archive");
        if (manifest.Schema != 1) throw new InvalidDataException("Vector.Editor.Error.Schema");
        if (manifest.Sheets is null || manifest.Objects is null || manifest.Sheets.Any(x=>x is null) || manifest.Objects.Any(x=>x is null)
            || zip.Entries.Count != 1 + manifest.Sheets.Length + manifest.Objects.Length)
            throw new InvalidDataException("Vector.Editor.Error.Archive");
        var project = new VectorProject { Schema = 1, Id = manifest.Id, Name = manifest.Name, CreatedUtc = manifest.CreatedUtc,
            ModifiedUtc = manifest.ModifiedUtc, ActiveSheetId = manifest.ActiveSheetId };
        foreach (var item in manifest.Sheets)
        {
            if (item.File != "sheets/" + item.Id + ".svg") throw new InvalidDataException("Vector.Editor.Error.Archive");
            project.Sheets.Add(new(item.Id, item.Name, Read(zip, item.File), item.Zoom, item.PanX, item.PanY));
        }
        foreach (var item in manifest.Objects)
        {
            if (item.File != "objects/" + item.Id + ".svg") throw new InvalidDataException("Vector.Editor.Error.Archive");
            project.Objects.Add(new(item.Id, item.Name, Read(zip, item.File), item.JobId));
        }
        Validate(project); return project;
    }

    public static void Validate(VectorProject project)
    {
        if (project.Schema != 1 || !Guid.TryParseExact(project.Id, "N", out _) || project.Name is null || project.Name.Length > 1024
            || project.Sheets.Count == 0 || !project.Sheets.Any(x => x.Id == project.ActiveSheetId)) throw new InvalidDataException("Vector.Editor.Error.Archive");
        var ids = new HashSet<string>(StringComparer.Ordinal); long bytes = 0;
        foreach (var item in project.Sheets.Select(s => (s.Id, s.Name, s.Svg)).Concat(project.Objects.Select(s => (s.Id, s.Name, s.Svg))))
        {
            if (!Guid.TryParseExact(item.Id, "N", out _) || !ids.Add(item.Id) || item.Name is null || item.Name.Length > 1024
                || (bytes += Utf8.GetByteCount(item.Svg)) > MaxBytes) throw new InvalidDataException("Vector.Editor.Error.Archive");
            _ = VectorSvg.Parse(item.Svg);
        }
        if (project.Sheets.Any(s => !double.IsFinite(s.Zoom) || s.Zoom is < .01 or > 128 || !double.IsFinite(s.PanX) || !double.IsFinite(s.PanY)))
            throw new InvalidDataException("Vector.Editor.Error.Archive");
    }
    private static void Write(ZipArchive zip, string name, string text)
    { using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), Utf8); writer.Write(text); }
    private static string Read(ZipArchive zip, string name, long limit = 32L * 1024 * 1024)
    {
        var entry = zip.GetEntry(name) ?? throw new InvalidDataException("Vector.Editor.Error.Archive");
        if (entry.Length > limit) throw new InvalidDataException("Vector.Editor.Error.Archive");
        using var stream = entry.Open(); using var memory = new MemoryStream(); var buffer = new byte[65536]; int count;
        while ((count = stream.Read(buffer)) != 0) { if (memory.Length + count > limit) throw new InvalidDataException("Vector.Editor.Error.Archive"); memory.Write(buffer, 0, count); }
        if (memory.Length != entry.Length) throw new InvalidDataException("Vector.Editor.Error.Archive");
        return Utf8.GetString(memory.ToArray());
    }
}
