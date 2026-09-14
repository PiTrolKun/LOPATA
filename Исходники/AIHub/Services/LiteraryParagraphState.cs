using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHub.Services;

public enum ParagraphStage { Request, Prepared, Result }
public sealed class ParagraphSelection
{
    public bool Selected { get; set; }
    public string Comment { get; set; } = "";
}
public sealed record ParagraphTurn(string Role, string Text);
public sealed record ParagraphRecommendation(string Id, string Reason);
public sealed class LiteraryParagraphState
{
    public int Version { get; set; } = 1;
    public string ProjectId { get; set; } = "";
    public string SessionId { get; set; } = Guid.NewGuid().ToString("N");
    public ParagraphStage Stage { get; set; }
    public string Request { get; set; } = "";
    public string Prepared { get; set; } = "";
    public string Result { get; set; } = "";
    public string RawResult { get; set; } = "";
    public string Failure { get; set; } = "";
    public bool Interrupted { get; set; }
    public string RouteId { get; set; } = "";
    public List<ParagraphTurn> History { get; set; } = [];
    public List<ParagraphRecommendation> Recommendations { get; set; } = [];
    public Dictionary<string, ParagraphSelection> Selection { get; set; } = [];
    public void RecordExchange(LiteraryChatProfile role, string task, string reply)
    {
        History.Add(new(role == LiteraryChatProfile.Writer ? "FinalTask" : "User", task));
        History.Add(new(role.ToString(), reply));
    }
    public void Next()
    {
        Stage = ParagraphStage.Request; Request = Prepared = Result = RawResult = Failure = "";
        Interrupted = false; Recommendations.Clear();
    }
    public void Clear()
    {
        Next(); History.Clear(); SessionId = Guid.NewGuid().ToString("N");
    }
}

public sealed class LiteraryParagraphStore(LiteraryProjectLayout layout)
{
    public string FilePath => Path.Combine(layout.Root, "Dialogs", "ParagraphFlow", "session.json");
    public LiteraryParagraphState Load()
    {
        layout.EnsurePresent(); LiteraryProjectLayout.CheckTreePath(Path.GetDirectoryName(FilePath)!);
        if (!File.Exists(FilePath)) return new() { ProjectId = layout.ProjectId };
        if ((File.GetAttributes(FilePath) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked session is unsupported.");
        var state = JsonSerializer.Deserialize<LiteraryParagraphState>(LiteraryChapterFiles.Read(FilePath), ParagraphJson.Options);
        if (state is null || state.Version != 1 || state.ProjectId != layout.ProjectId || !Enum.IsDefined(state.Stage)
            || state.Selection is null || state.History is null || state.Recommendations is null)
            throw new InvalidDataException("Invalid paragraph session. Original file retained.");
        return state;
    }
    public void Save(LiteraryParagraphState state)
    {
        layout.EnsurePresent();
        if (state.ProjectId != layout.ProjectId) throw new InvalidDataException("Foreign paragraph session.");
        layout.EnsureFolder("Dialogs/ParagraphFlow");
        LiteraryChapterFiles.Write(FilePath, ParagraphJson.Encode(state));
    }
}

public static class ParagraphJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };
    public static string Encode(object? value) => JsonSerializer.Serialize(value, Options);
}
