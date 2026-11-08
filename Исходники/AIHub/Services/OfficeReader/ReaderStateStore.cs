using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIHub.Services.OfficeReader;

public sealed record ReaderState(int Page = 0, double Zoom = 100, bool Book = false, bool? Dark = null);

internal static class ReaderStateStore
{
    private static string StatePath(string source)
    {
        var file = new FileInfo(source);
        var identity = Path.GetFullPath(source).ToUpperInvariant() + "\n" + file.Length + "\n" + file.LastWriteTimeUtc.Ticks;
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(AppDataPaths.BaseDirectory, "OfficeReader", "Positions", key + ".json");
    }
    internal static ReaderState Load(string source)
    {
        try { return JsonSerializer.Deserialize<ReaderState>(File.ReadAllText(StatePath(source))) ?? new(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    internal static void Save(string source, ReaderState state)
    {
        string? temporary = null;
        try
        {
            var path = StatePath(source); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(state)); File.Move(temporary, path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        finally
        {
            try { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}

public static class ReaderNavigation
{
    public static double ClampZoom(double zoom) => double.IsFinite(zoom) ? Math.Clamp(zoom, 25, 400) : 100;
    public static int ClampPage(int page, int count) => Math.Clamp(page, 0, Math.Max(0, count - 1));
    public static int SpreadStart(int page) => Math.Max(0, page / 2 * 2);
    public static int Next(int page, int count, bool book, int direction) =>
        ClampPage((book ? SpreadStart(page) : page) + direction * (book ? 2 : 1), count);
}
