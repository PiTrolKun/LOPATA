using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIHub.Services;

public sealed record LiteraryPlotAnchor(int Version, string ProjectId, string Role, string Text, string Revision, DateTimeOffset UpdatedAt, string Avoid = "")
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string Context => Avoid.Length == 0 ? Text : "Замысел и обязательные условия:\n" + Text + "\nЧего следует избегать:\n" + Avoid;
}
public sealed record LiteraryPlotAnchorChange(LiteraryPlotAnchor Before, LiteraryPlotAnchor After);
public sealed class LiteraryPlotAnchorException(Exception inner) : IOException("Plot anchor could not be read.", inner);

/// <summary>Author-owned plot plan. Change receipt is the future memory integration point.</summary>
public sealed class LiteraryPlotAnchorStore(LiteraryProjectLayout layout, LiteraryChatProfile role)
{
    public const int MaxCharacters = 3000;
    public string FilePath => Path.Combine(layout.Root, "Plot", role + ".json");
    public static string Revision(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static string Revision(string text, string avoid) => avoid.Length == 0 ? Revision(text) : Revision(JsonSerializer.Serialize(new[] { text, avoid }));
    public LiteraryPlotAnchor Load()
    {
        layout.EnsurePresent(); LiteraryProjectLayout.CheckTreePath(Path.GetDirectoryName(FilePath)!);
        if (!File.Exists(FilePath)) return new(1, layout.ProjectId, role.ToString(), "", Revision(""), DateTimeOffset.MinValue);
        if ((File.GetAttributes(FilePath) & FileAttributes.ReparsePoint) != 0) throw new IOException("Plot anchor links are not supported.");
        string json;
        try { json = LiteraryChapterFiles.Read(FilePath); }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("Invalid plot anchor encoding.", ex); }
        var result = JsonSerializer.Deserialize<LiteraryPlotAnchor>(json);
        if (result is null || result.Version is not (1 or 2) || result.ProjectId != layout.ProjectId || result.Role != role.ToString() || result.Text is null || result.Avoid is null
            || result.Text.Length + result.Avoid.Length > MaxCharacters || result.Revision != Revision(result.Text, result.Avoid))
            throw new InvalidDataException("Invalid plot anchor identity, length or revision.");
        return result;
    }
    public LiteraryPlotAnchorChange Save(string text, string expectedRevision)
        => Save(text, Load().Avoid, expectedRevision);
    public LiteraryPlotAnchorChange Save(string text, string avoid, string expectedRevision)
    {
        if (text.Length + avoid.Length > MaxCharacters) throw new InvalidDataException("Plot anchor is too long.");
        try { LiteraryChapterFiles.Utf8.GetByteCount(text); LiteraryChapterFiles.Utf8.GetByteCount(avoid); }
        catch (EncoderFallbackException ex) { throw new InvalidDataException("Invalid plot anchor text.", ex); }
        var before = Load();
        if (before.Revision != expectedRevision) throw new IOException("Plot anchor changed since it was opened.");
        if (before.Text == text && before.Avoid == avoid) return new(before, before);
        var after = new LiteraryPlotAnchor(2, layout.ProjectId, role.ToString(), text, Revision(text, avoid), DateTimeOffset.UtcNow, avoid);
        layout.EnsureFolder("Plot");
        LiteraryChapterFiles.Write(FilePath, JsonSerializer.Serialize(after));
        return new(before, after);
    }
}
