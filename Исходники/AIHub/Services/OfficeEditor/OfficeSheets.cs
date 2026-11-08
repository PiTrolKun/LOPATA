using System.IO;
using System.Text.Json.Serialization;

namespace AIHub.Services.OfficeEditor;

[JsonConverter(typeof(JsonStringEnumConverter<OfficeSheetKind>))]
internal enum OfficeSheetKind { Text, Book, Table, Presentation, Archive, ActivityLog, AiLog }

internal sealed record OfficeSheet(string Id, OfficeSheetKind Kind, string Name, OfficeMaterial[] Files)
{
    public OutputProvenance? SourceProvenance { get; init; }
    [JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; init; }
}
internal sealed record OfficeActivityEntry(DateTimeOffset AtUtc, string Action, string SheetId, string? Detail = null);

internal static class OfficeSheets
{
    internal const string PrimaryId = "primary", ActivityId = "activity", AiId = "ai";
    internal static bool UserKind(OfficeSheetKind kind) => kind is OfficeSheetKind.Text or OfficeSheetKind.Book
        or OfficeSheetKind.Table or OfficeSheetKind.Presentation or OfficeSheetKind.Archive;

    internal static OfficeSheetKind ClassifySource(string path) => Path.GetExtension(path).ToLowerInvariant() switch {
        ".docx" or ".doc" or ".odt" or ".rtf" or ".txt" or ".md" or ".markdown" or ".html" or ".htm" => OfficeSheetKind.Text,
        ".xlsx" or ".xls" or ".xlsm" or ".xlsb" or ".ods" or ".csv" or ".tsv" => OfficeSheetKind.Table,
        ".pptx" or ".ppt" or ".pptm" or ".odp" => OfficeSheetKind.Presentation,
        ".epub" or ".fb2" or ".mobi" or ".azw" or ".azw3" or ".kepub" or ".pdf" or ".djvu" or ".djv" or ".cbz" or ".cbr" => OfficeSheetKind.Book,
        _ => OfficeSheetKind.Archive };

    internal static string Payload(OfficeSheet sheet) => sheet.Id == PrimaryId && sheet.Kind == OfficeSheetKind.Text
        ? "content/document.docx" : "sheets/" + sheet.Id + (sheet.Kind == OfficeSheetKind.Text ? "/document.docx" : "/content.bin");

    internal static string ResultEntry(string id) => "history/results/" + id;
    internal static bool ValidId(string id) => !string.IsNullOrEmpty(id) && id.Length <= 96 &&
        id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    internal static bool SafeFileName(string name) => !string.IsNullOrWhiteSpace(name) && name is not ("." or "..")
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && Path.GetFileName(name) == name;
    internal static OfficeSheet New(string id, OfficeSheetKind kind, string name = "") => new(id, kind, name, []);

    internal static OfficeTextManifest EnsureAi(OfficeTextManifest manifest)
    {
        if (manifest.Sheets.Any(s => s.Kind == OfficeSheetKind.AiLog)) return manifest;
        return manifest with { Sheets = [..manifest.Sheets.Take(2), New(AiId, OfficeSheetKind.AiLog), ..manifest.Sheets.Skip(2)] };
    }

    internal static OfficeTextManifest Log(OfficeTextManifest manifest, string action, string sheetId, string? detail = null)
        => manifest with { Activity = [..manifest.Activity, new(DateTimeOffset.UtcNow, action, sheetId, detail)] };

    internal static OfficeAiEntry Legacy(OfficeAiAction action) => new(Guid.NewGuid().ToString("N"), PrimaryId, action.AtUtc,
        action.Action, "", null, null, "legacy", action.Model == null ? [] : [action.Model]);

    internal static OfficeTextManifest Initialize(OfficeTextManifest previous, OfficeSheetKind kind, string name, bool existing)
    {
        if (previous.Schema == 3) return previous;
        if (!UserKind(kind)) throw new InvalidDataException("OfficeEditor.Unsupported");
        var primary = New(PrimaryId, kind, name);
        var updated = previous with { Schema = 3, Kind = "workbook", Sheets = [primary, New(ActivityId, OfficeSheetKind.ActivityLog)] };
        if (previous.Materials.Length > 0)
            updated = updated with { Sheets = [..updated.Sheets, New("attachments", OfficeSheetKind.Archive) with { Files = previous.Materials }] };
        if (previous.AiActions.Length > 0)
            updated = EnsureAi(updated) with { AiEntries = previous.AiActions.Select(Legacy).ToArray() };
        return Log(updated, existing ? "Opened" : "Created", PrimaryId);
    }
}
