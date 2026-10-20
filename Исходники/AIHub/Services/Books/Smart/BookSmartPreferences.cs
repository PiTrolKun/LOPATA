using System.IO;
using System.Text.Json;

namespace AIHub.Services.Books.Smart;

internal static class BookSmartPreferences
{
    private static string PathName => Path.Combine(AppDataPaths.BaseDirectory, "Books", "smart-selection.json");
    internal static BookSmartSelection Load()
    {
        try
        {
            if (!File.Exists(PathName)) return new();
            var value = JsonSerializer.Deserialize<BookSmartSelection>(File.ReadAllBytes(PathName));
            return value is { OcrId: "paddle" or "ovis", VisionId: "alpha" or "beta" } ? value : new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    internal static void Save(BookSmartSelection value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        var pending = PathName + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(pending, JsonSerializer.SerializeToUtf8Bytes(value)); File.Move(pending, PathName, true); }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }
}
