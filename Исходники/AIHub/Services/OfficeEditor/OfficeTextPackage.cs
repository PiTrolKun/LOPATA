using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHub.Services.OfficeEditor;

internal sealed record OfficeMaterial(string Entry, string Name);
internal sealed record OfficeAiAction(DateTimeOffset AtUtc, string Action, OutputModelUse? Model);

internal sealed record OfficeTextManifest
{
    public string Format { get; init; } = "LOPATAdoxAI";
    public int Schema { get; init; } = 2;
    public string Kind { get; init; } = "text";
    public OutputProvenance? Provenance { get; init; }
    public string[] Reasons { get; init; } = [];
    public OfficeAiAction[] AiActions { get; init; } = [];
    public OfficeSheet[] Sheets { get; init; } = [];
    public OfficeActivityEntry[] Activity { get; init; } = [];
    public OfficeAiEntry[] AiEntries { get; init; } = [];
    public string? FormatReason { get; init; }
    public OfficeMaterial[] Materials { get; init; } = [];
    public OfficeMaterial? OriginalMaterial { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>The text surface owns one payload. Other materials pass through byte-for-byte,
/// including entries a newer version of this schema may introduce.</summary>
internal static class OfficeTextPackage
{
    internal const int MaxEntries = 512;
    internal const long MaxBytes = 512L * 1024 * 1024;
    private const int ManifestLimit = 4 * 1024 * 1024;
    private const int LogLimit = 64 * 1024 * 1024;
    private const string Activity = "history/actions.json", Ai = "history/requests.json";
    private const string Manifest = "manifest.json", Payload = "content/document.docx";
    private const string History = "history/ai.json";

    internal static OfficeTextManifest Inspect(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        if (zip.Entries.Count > MaxEntries) throw Invalid();
        foreach (var entry in zip.Entries)
        {
            bytes = checked(bytes + entry.Length);
            if (bytes > MaxBytes || !names.Add(entry.FullName) || entry.FullName.StartsWith('/') ||
                entry.FullName.Contains('\\') || entry.FullName.Contains(':') || entry.FullName.Split('/').Any(segment => segment is "." or ".."))
                throw Invalid();
        }
        var manifest = zip.GetEntry(Manifest);
        if (manifest is null || manifest.Length > ManifestLimit) throw Invalid();
        using var input = manifest.Open();
        var info = JsonSerializer.Deserialize<OfficeTextManifest>(input) ?? throw Invalid();
        if (info.Format != "LOPATAdoxAI" || (info.Schema is 1 or 2 ? info.Kind != "text" : info.Schema != 3 || info.Kind != "workbook") ||
            info.Materials == null || info.AiActions == null || info.Reasons == null || info.Reasons.Any(string.IsNullOrWhiteSpace) ||
            info.Materials.Any(m => m == null || string.IsNullOrEmpty(m.Entry) || !m.Entry.StartsWith("materials/", StringComparison.Ordinal)
                || zip.GetEntry(m.Entry) == null || string.IsNullOrEmpty(m.Name) || Path.GetFileName(m.Name) != m.Name))
            throw Invalid();
        if (info.Schema < 3 && zip.GetEntry(info.Schema == 1 ? "content/document.odt" : Payload) == null) throw Invalid();
        if (info.OriginalMaterial is { } original && (string.IsNullOrWhiteSpace(original.Name) ||
            Path.GetFileName(original.Name) != original.Name || string.IsNullOrEmpty(original.Entry) ||
            !original.Entry.StartsWith("originals/", StringComparison.Ordinal) || zip.GetEntry(original.Entry) == null)) throw Invalid();
        if (info.Schema >= 2 && zip.GetEntry(History) is { } history)
        {
            if (history.Length > LogLimit) throw Invalid();
            using var stream = history.Open();
            info = info with { AiActions = JsonSerializer.Deserialize<OfficeAiAction[]>(stream) ?? throw Invalid() };
        }
        if (info.Schema == 3)
        {
            info = info with { Activity = ReadLog<OfficeActivityEntry>(zip, Activity), AiEntries = ReadLog<OfficeAiEntry>(zip, Ai) };
            ValidateSheets(info, zip);
        }
        return info;
    }

    private static T[] ReadLog<T>(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path);
        if (entry == null) return [];
        if (entry.Length > LogLimit) throw Invalid();
        using var input = entry.Open();
        return JsonSerializer.Deserialize<T[]>(input) ?? throw Invalid();
    }

    private static void ValidateSheets(OfficeTextManifest info, ZipArchive zip)
    {
        var sheets = info.Sheets;
        if (zip.GetEntry(Activity) == null || sheets == null || sheets.Length < 2 || sheets.Length > MaxEntries ||
            sheets.Any(s => s == null || !OfficeSheets.ValidId(s.Id) || s.Name == null || s.Files == null ||
                s.Files.Any(m => m == null || !OfficeSheets.SafeFileName(m.Name) || string.IsNullOrEmpty(m.Entry) ||
                    !m.Entry.StartsWith("materials/", StringComparison.Ordinal) || zip.GetEntry(m.Entry) == null)) ||
            sheets.Select(s => s.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != sheets.Length ||
            sheets[0].Id != OfficeSheets.PrimaryId || !OfficeSheets.UserKind(sheets[0].Kind) ||
            sheets[1].Id != OfficeSheets.ActivityId || sheets[1].Kind != OfficeSheetKind.ActivityLog ||
            sheets.Any(s => s.Kind == OfficeSheetKind.Text && zip.GetEntry(OfficeSheets.Payload(s)) == null) ||
            sheets.Skip(2).Where((s, i) => !OfficeSheets.UserKind(s.Kind) &&
                !(i == 0 && s.Kind == OfficeSheetKind.AiLog && s.Id == OfficeSheets.AiId)).Any() ||
            (info.AiEntries.Length > 0) != sheets.Any(s => s.Kind == OfficeSheetKind.AiLog) ||
            info.AiEntries.Any(e => e == null || !OfficeSheets.ValidId(e.Id) || e.Models == null ||
                !sheets.Any(s => s.Id == e.SheetId) || (e.ResultName != null &&
                (!OfficeSheets.SafeFileName(e.ResultName) || zip.GetEntry(OfficeSheets.ResultEntry(e.Id)) == null))) ||
            info.AiEntries.Select(e => e.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != info.AiEntries.Length ||
            info.Activity.Any(e => e == null || string.IsNullOrWhiteSpace(e.Action) || !sheets.Any(s => s.Id == e.SheetId)))
            throw Invalid();
    }

    internal static void Store(string destination, byte[] document, OfficeTextManifest info, string? previous,
        string? addedFile = null, OfficeMaterial? addedMaterial = null, string? replaceEntry = null, byte[]? content = null)
    {
        var manifest = JsonSerializer.SerializeToUtf8Bytes(info with { AiActions = [], Activity = [], AiEntries = [] });
        var history = info.AiActions.Length == 0 ? null : JsonSerializer.SerializeToUtf8Bytes(info.AiActions);
        var actions = info.Schema == 3 ? JsonSerializer.SerializeToUtf8Bytes(info.Activity) : null;
        var requests = info.AiEntries.Length > 0 ? JsonSerializer.SerializeToUtf8Bytes(info.AiEntries) : null;
        var includeText = info.Schema < 3 || info.Sheets[0].Kind == OfficeSheetKind.Text;
        if (actions?.Length > LogLimit || requests?.Length > LogLimit || history?.Length > LogLimit || manifest.Length > ManifestLimit) throw Invalid();
        var staged = StagePath(destination);
        try
        {
            using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
                {
                    Write(archive, Manifest, manifest);
                    if (includeText) Write(archive, Payload, document);
                    if (history != null) Write(archive, History, history);
                    if (actions != null) Write(archive, Activity, actions);
                    if (requests != null) Write(archive, Ai, requests);
                    if (replaceEntry != null && content != null) Write(archive, replaceEntry, content);
                    long total = (long)manifest.Length + (includeText ? document.LongLength : 0) + (history?.Length ?? 0)
                        + (actions?.Length ?? 0) + (requests?.Length ?? 0) + (content?.LongLength ?? 0);
                    int count = 1 + (includeText ? 1 : 0) + (history == null ? 0 : 1) + (actions == null ? 0 : 1)
                        + (requests == null ? 0 : 1) + (content == null ? 0 : 1);
                    if (previous is not null)
                    {
                        using var source = ZipFile.OpenRead(previous);
                        var legacy = source.GetEntry("content/document.odt") != null && source.GetEntry(Payload) == null && source.GetEntry(Activity) == null;
                        foreach (var item in source.Entries)
                        {
                            if (item.FullName is Manifest or Payload || item.FullName == replaceEntry) continue;
                            if (info.Schema == 3 && item.FullName is Activity or Ai) continue;
                            if (!legacy && item.FullName == History) continue;
                            // Migrate the editable content to DOCX while keeping the exact
                            // Schema 1 ODT (including features the converter may not model).
                            var entryName = legacy && item.FullName == "content/document.odt" ? "originals/schema1-document.odt" : item.FullName;
                            if (legacy && item.FullName == "originals/schema1-document.odt" || entryName == addedMaterial?.Entry) throw Invalid();
                            if (++count > MaxEntries || (total = checked(total + item.Length)) > MaxBytes) throw Invalid();
                            using var reader = item.Open();
                            using var writer = archive.CreateEntry(entryName, CompressionLevel.Fastest).Open();
                            CopyEntry(reader, writer, item.Length);
                        }
                    }
                    if (addedFile != null && addedMaterial != null)
                    {
                        using var source = File.OpenRead(addedFile);
                        if (++count > MaxEntries || checked(total + source.Length) > MaxBytes) throw Invalid();
                        using var target = archive.CreateEntry(addedMaterial.Entry, CompressionLevel.Fastest).Open();
                        source.CopyTo(target);
                    }
                    if (total > MaxBytes) throw Invalid();
                }
                output.Flush(true);
            }
            Publish(staged, destination);
        }
        finally { File.Delete(staged); }
    }

    private static InvalidDataException Invalid() => new("OfficeEditor.InvalidPackage");
    internal static void CopyEntry(Stream input, Stream output, long expectedBytes)
    {
        var buffer = new byte[65536];
        int read;
        long written = 0;
        while ((read = input.Read(buffer)) != 0)
        {
            written += read;
            if (written > expectedBytes) throw Invalid();
            output.Write(buffer, 0, read);
        }
        if (written != expectedBytes) throw Invalid();
    }
    internal static string StagePath(string path) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!,
        ".lopata-" + Guid.NewGuid().ToString("N") + Path.GetExtension(path));
    internal static void Publish(string staged, string target)
    {
        if (File.Exists(target)) File.Replace(staged, target, target + ".bak");
        else File.Move(staged, target);
    }

    private static void Write(ZipArchive archive, string name, byte[] bytes)
    {
        using var stream = archive.CreateEntry(name, CompressionLevel.Fastest).Open();
        stream.Write(bytes);
    }
}
