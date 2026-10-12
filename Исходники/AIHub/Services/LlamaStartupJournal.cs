using System.IO;
using System.Text.Json;

namespace AIHub.Services;

/// <summary>Startup evidence without song prompts; disk errors never replace a model outcome.</summary>
internal sealed class LlamaStartupJournal
{
    private readonly object _gate = new();
    internal string Path { get; } = System.IO.Path.Combine(AppDataPaths.BaseDirectory, "Diagnostics", "LlamaStartup",
        DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N") + ".jsonl");
    internal void Record(string kind, object? data)
    {
        try
        {
            var line = JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow, kind, data });
            lock (_gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.AppendAllText(Path, line + "\n", new System.Text.UTF8Encoding(false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }
    internal void Capture(string stream, string line) => Record(stream,
        line.Length <= 8192 ? line : line[..8192] + " [truncated]");
}
