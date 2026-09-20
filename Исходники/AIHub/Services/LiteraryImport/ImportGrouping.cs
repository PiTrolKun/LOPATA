using System.IO;
using System.Text.Json;

namespace AIHub.Services.LiteraryImport;

public static class ImportGrouping
{
    private static string Key(ImportDecision[] original) => "user-grouping/" + ImportSession.Hash(JsonSerializer.Serialize(original, ImportJson.Options));
    public static ImportDecision[] Read(ImportSession session, ImportDecision[] original)
    {
        var edited = session.ReadLast<ImportDecision[]>(Key(original));
        if (edited is null) return original;
        Validate(original, edited); return edited;
    }
    public static void Save(ImportSession session, ImportDecision[] original, ImportDecision[] edited)
    {
        if (session.State.PlannedPath.Length > 0) throw new InvalidDataException("Literary.Import.LockedSelection");
        Validate(original, edited);
        session.AddJson(Key(original), edited, session.State.Artifacts.Last(a => a.Step.StartsWith("pass1/", StringComparison.Ordinal)).Id);
    }
    private static void Validate(ImportDecision[] original, ImportDecision[] edited)
    {
        if (original.Length != edited.Length || original.Where((d, i) => edited[i].Project.Length > 150
            || d != (edited[i] with { Project = d.Project })).Any()) throw new InvalidDataException("Literary.Import.Corrupt");
    }
}
