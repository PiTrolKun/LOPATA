using System.IO;
using System.Text.Json;

namespace AIHub.Services.LiteraryImport;

public sealed record ImportBookEditorState
{
    public int Version { get; init; } = 1;
    public string Revision { get; init; } = "";
    public int SelectionStart { get; init; }
    public int SelectionLength { get; init; }
    public int TopCharacter { get; init; }
    public double TopPixel { get; init; }
    public double VerticalOffset { get; init; }
    public string Search { get; init; } = "";
    public double FontSize { get; init; } = 18;
    public double Left { get; init; }
    public double Top { get; init; }
    public double Width { get; init; } = 1120;
    public double Height { get; init; } = 780;
    public bool Maximized { get; init; }
    public bool ContentsVisible { get; init; } = true;
    public string ContentsId { get; init; } = "";
    public double ContentsOffset { get; init; }

    public ImportBookEditorState Clamp(int length) => this with
    {
        SelectionStart = Math.Clamp(SelectionStart, 0, length),
        SelectionLength = Math.Clamp(SelectionLength, 0, length - Math.Clamp(SelectionStart, 0, length)),
        TopCharacter = Math.Clamp(TopCharacter, 0, length),
        Search = Search ?? "",
        FontSize = double.IsFinite(FontSize) ? Math.Clamp(FontSize, 12, 32) : 18,
        TopPixel = double.IsFinite(TopPixel) ? Math.Clamp(TopPixel, -100, 100) : 0,
        VerticalOffset = double.IsFinite(VerticalOffset) ? Math.Max(0, VerticalOffset) : 0,
        ContentsOffset = double.IsFinite(ContentsOffset) ? Math.Max(0, ContentsOffset) : 0,
        Width = double.IsFinite(Width) ? Math.Clamp(Width, 680, 10000) : 1120,
        Height = double.IsFinite(Height) ? Math.Clamp(Height, 440, 10000) : 780
    };

    public static ImportBookEditorState? Load(string root)
    {
        var path = Path.Combine(root, "Import", "book-editor-state.json");
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            try
            {
                if (File.Exists(candidate) && JsonSerializer.Deserialize<ImportBookEditorState>(
                    LiteraryChapterFiles.Read(candidate), ImportJson.Options) is { Version: 1 } state) return state;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return null;
    }

    public void Save(string root)
    {
        new LiteraryProjectLayout(root).EnsurePresent();
        LiteraryChapterFiles.Write(Path.Combine(root, "Import", "book-editor-state.json"),
            JsonSerializer.Serialize(this, ImportJson.Options));
    }
}
