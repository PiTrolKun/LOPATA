using System.Text.Json;

namespace AIHub.Services.VectorEditor;

public sealed record VectorSheet(string Id, string Name, string Svg, double Zoom = 1, double PanX = 0, double PanY = 0);
public sealed record VectorObject(string Id, string Name, string Svg, string? JobId = null);

/// <summary>The complete editable document. Source paths and model runtimes are not project dependencies.</summary>
public sealed record VectorProject
{
    public int Schema { get; init; } = 1;
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ModifiedUtc { get; set; } = DateTimeOffset.UtcNow;
    public string ActiveSheetId { get; set; } = "";
    public List<VectorSheet> Sheets { get; init; } = [];
    public List<VectorObject> Objects { get; init; } = [];
    public VectorProject Copy() => this with { Sheets = Sheets.ToList(), Objects = Objects.ToList() };
    public static VectorProject New(string name, string sheetName)
    {
        var sheet = new VectorSheet(Guid.NewGuid().ToString("N"), sheetName, VectorEditorSvg.Blank(1000, 1000));
        return new() { Name = name, ActiveSheetId = sheet.Id, Sheets = [sheet] };
    }
}

/// <summary>Transactions include sheets and the object library, so cross-sheet operations are undoable together.</summary>
public sealed class VectorProjectHistory
{
    private readonly LinkedList<VectorProject> _undo = new();
    private readonly Stack<VectorProject> _redo = new();
    private long _bytes;
    private const long Budget = 96L * 1024 * 1024;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public void Record(VectorProject before)
    {
        var copy = before.Copy(); _undo.AddLast(copy); _bytes += Size(copy); _redo.Clear();
        while (_bytes > Budget && _undo.Count > 1) { _bytes -= Size(_undo.First!.Value); _undo.RemoveFirst(); }
    }
    public VectorProject Undo(VectorProject current)
    {
        if (!CanUndo) return current;
        _redo.Push(current.Copy()); var value = _undo.Last!.Value; _undo.RemoveLast(); _bytes -= Size(value); return value.Copy();
    }
    public VectorProject Redo(VectorProject current)
    {
        if (!CanRedo) return current;
        var value = _redo.Pop(); _undo.AddLast(current.Copy()); _bytes += Size(current); return value.Copy();
    }
    private static long Size(VectorProject value) => value.Sheets.Sum(x => (long)x.Svg.Length * 2) + value.Objects.Sum(x => (long)x.Svg.Length * 2);
}
