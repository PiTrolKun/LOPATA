using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.VisualScenario;

internal sealed record VisualFragment(string Id, string Text, bool Selected = false);
internal sealed record VisualImage(string Id, string Name, string LocalName, bool Selected = false);
internal sealed record VisualMessage(string Id, bool User, string Text, VisualFragment[] Fragments, string[] Images);
internal sealed record VisualInput(string ImageId, string Role, bool IsBase = false);
internal sealed record VisualPrompt(string Text, VisualInput[] Images, OutputModelUse? Advisor = null)
{
    internal static IEqualityComparer<VisualPrompt> ContentComparer { get; } = EqualityComparer<VisualPrompt>.Create(
        (left, right) => ReferenceEquals(left, right) || left is not null && right is not null
            && string.Equals(left.Text, right.Text, StringComparison.Ordinal) && left.Images.SequenceEqual(right.Images),
        prompt =>
        {
            var hash = new HashCode();
            hash.Add(prompt.Text, StringComparer.Ordinal);
            foreach (var input in prompt.Images) hash.Add(input);
            return hash.ToHashCode();
        });
}
internal sealed record VisualResult(string Id, VisualPrompt Prompt, string Result, DateTimeOffset At);
internal sealed record VisualPromptCard(int Number, VisualResult[] Results);
internal sealed record VisualJob(string ProjectId, VisualPrompt Prompt, ImageGenerationRequest Request)
{
    public bool Standalone { get; init; }
}

internal sealed class VisualProject
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public string Name { get; set; } = "";
    public bool Manual { get; set; }
    public bool Persistent { get; set; }
    public string Advisor { get; set; } = VisualComponents.Core;
    public string Artist { get; set; } = VisualComponents.Base;
    public string Draft { get; set; } = "";
    public string Folder { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    public string Aspect { get; set; } = "1:1";
    public int Size { get; set; } = 1024;
    public ImageGenerationFileFormat OutputFormat { get; set; }
    public List<string> ManualLines { get; init; } = [];
    public List<VisualMessage> Messages { get; init; } = [];
    public List<VisualImage> Images { get; init; } = [];
    public List<string> DraftImages { get; init; } = [];
    public List<VisualResult> Results { get; init; } = [];
    public string? ActivePrompt { get; set; }
    public string? BaseImage { get; set; }
    public string? PromptDraft { get; set; }
    public string? PromptRevisionId { get; set; }
    public OutputModelUse? PromptAdvisor { get; set; }
    public VisualInput[]? PreviewImages { get; set; }
    public string? Summary { get; set; }
    public HashSet<string> Hidden { get; init; } = new(StringComparer.Ordinal);
    public List<StudioContextChange> ContextHistory { get; init; } = [];

    public bool HasWork => Messages.Count > 0 || Draft.Length > 0 || ManualLines.Count > 0
        || Results.Count > 0 || Images.Count > 0 || Name.Length > 0;
    public VisualResult? SelectedPrompt => Results.FirstOrDefault(result => result.Id == ActivePrompt);
    internal string? DraftForRevision => Results.FirstOrDefault(result => result.Id == PromptRevisionId)?.Prompt.Text
        ?? (PreviewImages is null ? PromptDraft : null);

    internal VisualPromptCard[] PromptCards() => Results.GroupBy(result => result.Prompt, VisualPrompt.ContentComparer)
        .Select((group, index) => new VisualPromptCard(index + 1, group.ToArray())).ToArray();
}

internal sealed class VisualProjects(string root)
{
    internal static VisualProjects Default => new(Path.Combine(AppDataPaths.BaseDirectory, "VisualScenario"));
    private string CurrentPath => Path.Combine(root, "current.json");

    internal string DirectoryFor(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Visual.InvalidProject");
        return Path.Combine(root, id);
    }

    internal void EnsureDrawingSession(VisualJob job)
    {
        var directory = DirectoryFor(job.ProjectId);
        if (!Path.GetFullPath(job.Request.SessionDirectory).Equals(Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Visual.InvalidProject");
        ImageGenerationSessionStore.EnsureInitialized(directory, job.ProjectId);
    }

    internal string ImagePath(VisualProject project, string id)
    {
        var image = project.Images.FirstOrDefault(image => image.Id == id) ?? throw new InvalidDataException("Visual.MissingImage");
        return Path.Combine(DirectoryFor(project.Id), "images", Path.GetFileName(image.LocalName));
    }

    internal VisualImage Attach(VisualProject project, string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp"))
            throw new InvalidDataException("Visual.InvalidImage");
        var id = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(DirectoryFor(project.Id), "images");
        Directory.CreateDirectory(directory);
        var image = new VisualImage(id, Path.GetFileName(path), id + extension);
        File.Copy(path, Path.Combine(directory, image.LocalName));
        project.Images.Add(image);
        return image;
    }

    internal VisualProject? Current()
    {
        if (!File.Exists(CurrentPath)) return null;
        var id = Read<string>(CurrentPath);
        return Guid.TryParseExact(id, "N", out _) && File.Exists(ProjectPath(id)) ? Load(id) : null;
    }

    internal void Save(VisualProject project, bool current = true)
    {
        Write(ProjectPath(project.Id), project);
        if (current) Write(CurrentPath, project.Id);
    }

    internal VisualProject Load(string id) => Read<VisualProject>(ProjectPath(id));

    internal IReadOnlyList<VisualProject> List()
    {
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateDirectories(root).Select(Path.GetFileName)
            .Where(id => Guid.TryParseExact(id, "N", out _) && File.Exists(ProjectPath(id!)))
            .Select(id => Load(id!)).Where(project => project.Persistent)
            .OrderByDescending(project => project.CreatedAt).ToArray();
    }

    private string ProjectPath(string id) => Path.Combine(DirectoryFor(id), "project.json");
    private static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllBytes(path))
        ?? throw new InvalidDataException("Visual.InvalidProject");

    private static void Write<T>(string path, T data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(output, data);
                output.Flush(true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
