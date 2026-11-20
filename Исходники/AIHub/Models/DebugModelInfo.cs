namespace AIHub.Models;

public sealed class DebugModelInfo
{
    public string Name { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public string Role { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string Format { get; set; } = string.Empty;

    public bool IsCoreModel { get; set; }

    public bool IsRunnable { get; set; } = true;

    public string SemanticDescriptionRu { get; set; } = string.Empty;

    public string SemanticDescriptionEn { get; set; } = string.Empty;

    internal DebugModelInfo WithPath(string path)
    {
        if (string.Equals(Path, path, StringComparison.OrdinalIgnoreCase)) return this;
        var model = (DebugModelInfo)MemberwiseClone();
        model.Path = path;
        return model;
    }

    public override string ToString()
    {
        var role = string.IsNullOrWhiteSpace(Role) ? "no manifest" : Role;
        var status = string.IsNullOrWhiteSpace(Status) ? "unknown" : Status;
        var format = string.IsNullOrWhiteSpace(Format) ? "model" : Format;
        var runState = IsRunnable ? "runnable" : "tool-only";
        return $"{Name} ({FormatSize(SizeBytes)}, {format}, {role}, {status}, {runState})";
    }

    private static string FormatSize(long bytes)
    {
        var value = Math.Max(0, bytes);
        string[] units = ["B", "KB", "MB", "GB"];
        var unitIndex = 0;
        var display = (double)value;
        while (display >= 1024 && unitIndex < units.Length - 1)
        {
            display /= 1024;
            unitIndex++;
        }

        return $"{display:0.##} {units[unitIndex]}";
    }
}
