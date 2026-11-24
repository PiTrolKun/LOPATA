using System.IO;
using System.Text.Json;

namespace AIHub.Services.NeuroConstructor;

public sealed record NeuroExample(string Image, string Text, int Slot, string[] Audio, string Split);
public sealed record NeuroStatus(string Status, string Stage, int Step, int Total, double ElapsedSeconds,
    double? RemainingSeconds, string Message);

public sealed record NeuroProject
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool ExternalAgentProject { get; init; }
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Mode { get; init; } = "pro";
    public string Composition { get; set; } = "pair";
    public string TaskName { get; set; } = "";
    public bool UseReferenceExamples { get; set; } = true;
    public string Source { get; set; } = "";
    public string ParentPackage { get; set; } = "";
    public string ImportedFrom { get; set; } = "";
    public string Device { get; set; } = "auto";
    public string LanguageDevice { get; set; } = "";
    public string VisionDevice { get; set; } = "";
    public string SpeechDevice { get; set; } = "";
    public bool AutoEvaluateOnly { get; set; } = true;
    public int Cycles { get; set; } = 1;
    public int Attempts { get; set; } = 1;
    public int MemoryMiB { get; set; }
    public string Connection { get; set; } = "inside";
    public string AudioPolicy { get; set; } = "needed";
    public string Channel { get; set; } = "continuous";
    public List<NeuroExample> Examples { get; init; } = [];
}

internal static class NeuroFiles
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly AsyncLocal<string?> TestRoot = new();
    internal static string Root => TestRoot.Value ?? Path.Combine(AppDataPaths.BaseDirectory, "NeuroConstructor");
    internal static IDisposable UseRoot(string directory)
    {
        var before = TestRoot.Value;
        TestRoot.Value = Path.GetFullPath(directory);
        return new RestoreRoot(before);
    }
    private sealed class RestoreRoot(string? previous) : IDisposable
    {
        public void Dispose() => TestRoot.Value = previous;
    }
    internal static string Project(string id) => Path.Combine(Root,"projects", Key(id));
    internal static string Run(string project, string run) => Path.Combine(Project(project),"runs",Key(run));
    private static string Key(string id) => Guid.TryParseExact(id,"N",out var value)
        ? value.ToString("N") : throw new InvalidDataException("Neuro.ProjectId");
    internal static T Read<T>(string path)
    {
        using var input = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<T>(input,Json) ?? throw new InvalidDataException(Path.GetFileName(path));
    }
    internal static void Write<T>(string path,T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {
                JsonSerializer.Serialize(output,value,Json);
                output.Flush(true);
            }
            File.Move(temporary,path,true);
        }
        finally { File.Delete(temporary); }
    }
}
