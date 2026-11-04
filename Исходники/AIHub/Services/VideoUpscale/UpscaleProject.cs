using System.IO;
using System.Text.Json;

namespace AIHub.Services.VideoUpscale;

internal enum UpscaleMode { Manual, Balanced, Heavy }
internal enum UpscaleEngine { Scale, Flash, NeuralRendering, RtxVideo, VfxVsr, VfxUpscale, DlssSr }

internal sealed class UpscaleStep
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public UpscaleEngine Engine { get; set; }
    public bool Enabled { get; set; } = true;
    public int Width { get; set; } = 3840;
    public int Height { get; set; } = 2160;
    public string Algorithm { get; set; } = "lanczos";
    public double Mix { get; set; } = 1;
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.Ordinal);
    public string Get(string key, string fallback) => Parameters.GetValueOrDefault(key, fallback);
}

internal sealed class UpscaleMedia
{
    public int Width { get; set; }
    public int Height { get; set; }
    public double Duration { get; set; }
    public double Fps { get; set; } = 24;
    public string Rate { get; set; } = "24/1";
    public long Frames { get; set; }
    public int AudioStreams { get; set; }
}

internal sealed class UpscaleProject
{
    public int Schema { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";
    public long SourceLength { get; set; }
    public string SourceSha256 { get; set; } = "";
    public UpscaleMedia Media { get; set; } = new();
    public UpscaleMode Mode { get; set; } = UpscaleMode.Balanced;
    public List<UpscaleStep> Steps { get; set; } = [];
    public double PreviewStart { get; set; }
    public double PreviewLength { get; set; } = 5;
    public string? PreviewPath { get; set; }
    public string? PreviewFingerprint { get; set; }
    public string ExportPath { get; set; } = "";
    public string Codec { get; set; } = "libopenh264";
    public int BitrateMbps { get; set; } = 40;
    public int CpuThreads { get; set; }
    public double ChunkSeconds { get; set; } = 5;
    public string Workspace { get; set; } = "";
    public string? LastJob { get; set; }
    public DateTimeOffset Modified { get; set; } = DateTimeOffset.UtcNow;
}

internal sealed class UpscaleJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public UpscaleProject Project { get; set; } = new();
    public bool Preview { get; set; }
    public long StartFrame { get; set; }
    public long Frames { get; set; }
    public int ChunkFrames { get; set; }
    public string Output { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public bool Complete { get; set; }
    public UpscalePart? FinalArtifact { get; set; }
    public Dictionary<string, UpscalePart> Parts { get; set; } = [];
    public List<object> Components { get; set; } = [];
}

internal sealed record UpscalePart(string Path, string Sha256, long Frames, int Width, int Height);
internal sealed record UpscaleCheckpoint(string JobFile);
internal sealed record UpscaleProgress(double Fraction, string Detail);

internal static class UpscaleStore
{
    private static readonly AsyncLocal<string?> TestRoot = new();
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    internal static string Root => TestRoot.Value ?? Path.Combine(AppDataPaths.BaseDirectory, "VideoUpscale");
    private static readonly string ConfiguredWorkRoot = Path.Combine(new StorageSettingsStore().LoadOrCreate().Results.Locations.Select(l => l.Path?.Trim()).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)) ?? Path.Combine(AppDataPaths.BaseDirectory, "VideoUpscale"), "VideoUpscaleWork");
    internal static string WorkRoot => TestRoot.Value is {} test ? Path.Combine(test, "Work") : ConfiguredWorkRoot;
    internal static IDisposable UseTestRoot(string root) { var previous = TestRoot.Value; TestRoot.Value = Path.GetFullPath(root); return new TestScope(() => TestRoot.Value = previous); }
    private sealed class TestScope(Action restore) : IDisposable { public void Dispose() => restore(); }
    internal static string ProjectPath(string id) => Path.Combine(Root, "Projects", Id(id) + ".lopataUpscale");
    internal static string Work(string id) => Path.Combine(WorkRoot, Id(id));
    private static string Id(string id) => Guid.TryParseExact(id, "N", out _) ? id : throw new InvalidDataException("VideoUpscale.InvalidProject");
    internal static string JobPath(UpscaleJob job) => Path.Combine(Work(job.Project.Id), "Jobs", Id(job.Id), "job.json");
    internal static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)!;
    internal static void Save<T>(string path, T value)
    {
        path = Path.GetFullPath(path); Lopata.Updates.SafeUpdatePath.RejectLinks(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                JsonSerializer.Serialize(stream, value, Json); stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temp, path, path + ".previous", true);
            else File.Move(temp, path);
        } finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    internal static T Load<T>(string path)
    {
        try { return Read<T>(path); }
        catch (Exception e) when (e is JsonException or IOException) {
            if (!File.Exists(path + ".previous")) throw;
            return Read<T>(path + ".previous");
        }
    }
    private static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("VideoUpscale.InvalidProject");
    internal static void SaveProject(UpscaleProject project)
    { project.Modified = DateTimeOffset.UtcNow; Save(ProjectPath(project.Id), project); }
    internal static IReadOnlyList<UpscaleProject> Projects() => !Directory.Exists(Path.Combine(Root, "Projects")) ? [] :
        Directory.EnumerateFiles(Path.Combine(Root, "Projects"), "*.lopataUpscale").Select(TryProject).OfType<UpscaleProject>().OrderByDescending(x => x.Modified).ToArray();
    private static UpscaleProject? TryProject(string path) { try { return Load<UpscaleProject>(path); } catch { return null; } }
}
