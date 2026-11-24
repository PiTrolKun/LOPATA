using System.IO;
using System.Text.Json;
using AIHub.Models;
using Lopata.Recovery;
using Lopata.Updates;

namespace AIHub.Services.ExternalAgents;

internal sealed class AgentDiagnosticsSources
{
    internal static readonly string[] Names = ["components", "processes", "recovery", "updates", "core", "debug", "uncertainty", "literary", "llama"];
    private const int MaxFileBytes = 2 * 1024 * 1024;
    private const int MaxSourceBytes = 8 * MaxFileBytes;
    private readonly Dictionary<string, List<(string Directory, string Pattern)>> _locations = [];
    private readonly HashSet<string> _unavailable = [];
    private string? _uncertaintyProjects;

    internal void Add(string source, string directory, string pattern)
    {
        if (!_locations.TryGetValue(source, out var list)) _locations[source] = list = [];
        list.Add((directory, pattern));
    }

    internal static AgentDiagnosticsSources Local()
    {
        var sources = new AgentDiagnosticsSources();
        sources.Add("components", AppDataPaths.ComponentLogsDirectory, "components-????-??.jsonl");
        sources.Add("updates", Path.Combine(AppDataPaths.BaseDirectory, "Diagnostics", "Updates"), "download-*.json");
        sources.Add("llama", Path.Combine(AppDataPaths.BaseDirectory, "Diagnostics", "LlamaStartup"), "*.jsonl");
        foreach (var role in new[] { "LiteraryAdvisor", "LiteraryAdvisorGPU", "LiteraryWriter", "LiteraryWriterCPU" })
            sources.Add("literary", Path.Combine(LiteraryRequestDiagnostics.Root, role), "*.jsonl");
        sources.Add("processes", OwnedProcessRegistry.LogDirectory, "processes.jsonl");
        sources.Add("processes", OwnedProcessRegistry.LogDirectory, "processes.jsonl.previous");
        var recoveryFile = RecoveryTask.GetEventsPath(Path.Combine(AppContext.BaseDirectory, "AIHub.exe"), AppDataPaths.BaseDirectory);
        sources.Add("recovery", Path.GetDirectoryName(recoveryFile)!, Path.GetFileName(recoveryFile));
        sources.Add("recovery", Path.GetDirectoryName(recoveryFile)!, Path.GetFileName(recoveryFile) + ".previous");

        var storage = new StorageSettings();
        try
        {
            // LoadOrCreate writes defaults and creates directories; diagnostic reads must work without either.
            if (File.Exists(AppDataPaths.StorageSettingsPath))
            {
                SafeUpdatePath.RejectLinks(AppDataPaths.StorageSettingsPath);
                using var stream = Open(AppDataPaths.StorageSettingsPath);
                if (stream.Length > MaxFileBytes) throw new InvalidDataException();
                storage = JsonSerializer.Deserialize<StorageSettings>(stream, JsonSerializerOptions.Web) ?? throw new InvalidDataException();
                if (storage.Results?.Locations is null || storage.Results.Locations.Any(location => location == null))
                    throw new InvalidDataException();
            }
            var root = storage.Results.Locations.Select(location => location.Path?.Trim())
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
            if (root != null && !Path.IsPathFullyQualified(root)) throw new InvalidDataException();
            sources.Add("core", SessionPathService.GetCoreSessionsDirectory(storage), "*.jsonl");
            sources.Add("debug", SessionPathService.GetDebugModelTesterSessionsDirectory(storage), "*.jsonl");
            var scenarioRoot = Path.Combine(root == null ? AppDataPaths.BaseDirectory : Path.Combine(root, "AI_HUB"), "Scenarios", "Uncertainty");
            sources._uncertaintyProjects = Path.Combine(scenarioRoot, "Projects");
            sources.Add("uncertainty", Path.Combine(scenarioRoot, "Sessions"), "*.jsonl");
            sources.Add("uncertainty", Path.Combine(scenarioRoot, "Executors"), "*.jsonl");
            if (root != null)
                sources.Add("uncertainty", Path.Combine(AppDataPaths.BaseDirectory, "Scenarios", "Uncertainty", "Sessions"), "*.jsonl");
        }
        catch (Exception ex) when (FileError(ex) || ex is JsonException)
        {
            sources._unavailable.UnionWith(["core", "debug", "uncertainty"]);
        }
        return sources;
    }

    internal AgentDiagnosticSnapshot Scan(string? selected, DateTimeOffset? until = null, string? selectedFile = null, long position = 0)
    {
        var snapshot = new AgentDiagnosticSnapshot();
        foreach (var source in Names.Where(name => selected == null || name == selected))
        {
            if (_unavailable.Contains(source)) snapshot.Unavailable.Add(source);
            var files = new List<FileInfo>();
            var locations = _locations.TryGetValue(source, out var known) ? known.ToList() : [];
            if (source == "uncertainty" && _uncertaintyProjects != null)
            {
                try
                {
                    SafeUpdatePath.RejectLinks(_uncertaintyProjects);
                    foreach (var path in Directory.EnumerateDirectories(_uncertaintyProjects, "session_*"))
                        locations.Add((Path.Combine(path, "Logs"), "*.jsonl"));
                }
                catch (DirectoryNotFoundException) { }
                catch (Exception ex) when (FileError(ex)) { snapshot.Unavailable.Add(source); }
            }
            foreach (var (directory, pattern) in locations)
            {
                try
                {
                    SafeUpdatePath.RejectLinks(directory);
                    files.AddRange(new DirectoryInfo(directory).EnumerateFiles(pattern));
                }
                catch (DirectoryNotFoundException) { }
                catch (Exception ex) when (FileError(ex)) { snapshot.Unavailable.Add(source); }
            }
            if (files.Count == 0) snapshot.Unavailable.Add(source);
            var budget = MaxSourceBytes;
            var visited = 0;
            // An old incident must remain discoverable even after many newer sessions.
            var ordered = files.OrderByDescending(file => selectedFile != null &&
                    AgentDiagnosticsProjection.FileKey(file.FullName) == selectedFile)
                .ThenBy(file => until is { } end ? Math.Abs((end.UtcDateTime - file.LastWriteTimeUtc).Ticks) : -file.LastWriteTimeUtc.Ticks);
            foreach (var file in ordered)
            {
                if (budget <= 0 || visited++ >= 64)
                {
                    snapshot.Limited.Add(source);
                    break;
                }
                try
                {
                    SafeUpdatePath.RejectLinks(file.FullName);
                    using var stream = Open(file.FullName);
                    var length = stream.Length;
                    var bytes = (int)Math.Min(length, Math.Min(MaxFileBytes, budget));
                    budget -= bytes;
                    var fileKey = AgentDiagnosticsProjection.FileKey(file.FullName);
                    var origin = length - bytes;
                    if (origin > 0)
                    {
                        snapshot.Limited.Add(source);
                        if (selectedFile == fileKey)
                            origin = Math.Clamp(position - bytes / 2L, 0, origin);
                        else if (until is { } target)
                            origin = WindowStart(stream, target, bytes);
                    }
                    stream.Position = Math.Max(0, origin - 1);
                    var partialLine = origin > 0 && stream.ReadByte() != (byte)'\n';
                    var buffer = new byte[bytes];
                    stream.ReadExactly(buffer);
                    snapshot.FilesRead++;
                    for (int start = 0, next; start < bytes; start = next)
                    {
                        var newline = Array.IndexOf(buffer, (byte)'\n', start);
                        var end = newline < 0 ? bytes : newline;
                        next = newline < 0 ? bytes : newline + 1;
                        if (partialLine) { partialLine = false; continue; }
                        if (newline < 0 && origin + end < length) break;
                        var line = buffer.AsMemory(start, end - start);
                        if (origin + start == 0 && line.Span.StartsWith(System.Text.Encoding.UTF8.Preamble)) line = line[3..];
                        if (line.Length == 0 || line.Span.SequenceEqual("\r"u8)) continue;
                        try
                        {
                            using var json = JsonDocument.Parse(line);
                            var record = AgentDiagnosticsProjection.Project(source, fileKey, origin + start, json.RootElement);
                            if (record != null) snapshot.Events.Add(record);
                        }
                        catch (JsonException) { snapshot.InvalidRecords++; }
                    }
                }
                catch (Exception ex) when (FileError(ex)) { snapshot.Unavailable.Add(source); }
            }
        }
        return snapshot;
    }

    // Look for the requested time in a long chronological JSONL journal without reading whole transcripts.
    private static long WindowStart(FileStream stream, DateTimeOffset until, int bytes)
    {
        long low = 0, high = stream.Length;
        var sample = new byte[65536];
        while (high - low > bytes / 2)
        {
            var mid = low + (high - low) / 2;
            stream.Position = mid;
            var count = stream.Read(sample);
            var first = sample.AsSpan(0, count).IndexOf((byte)'\n');
            if (first < 0) break;
            var record = sample.AsMemory(first + 1, count - first - 1);
            var end = record.Span.IndexOf((byte)'\n');
            if (end < 0) break;
            try
            {
                using var document = JsonDocument.Parse(record[..end]);
                if (AgentDiagnosticsProjection.RecordTime(document.RootElement) is not { } stamp) break;
                if (stamp <= until) low = mid;
                else high = mid;
            }
            catch (JsonException) { break; }
        }
        var center = low + (high - low) / 2;
        return Math.Clamp(center - bytes / 2L, 0, stream.Length - bytes);
    }

    private static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static bool FileError(Exception error) => error is IOException or UnauthorizedAccessException
        or InvalidDataException or ArgumentException or NotSupportedException;
}
