using System.IO;
using System.Text;
using System.Text.Json;
using AIHub.Models.OneC;

namespace AIHub.Services.OneC;

internal interface IOneCCodeChecker
{
    string Id { get; }
    string Version => "1";
    Task<OneCCheck> CheckAsync(string code, string fileName, string language, CancellationToken token);
}

/// <summary>Runs a pinned static analyzer over a private snapshot, never formats or executes source code.</summary>
internal sealed class OneCBslChecker(OneCBslRuntime? runtime = null, string? executableForTest = null) : IOneCCodeChecker
{
    public string Id => OneCBslRuntime.ComponentId;
    public string Version => OneCBslRuntime.Version;
    public async Task<OneCCheck> CheckAsync(string code, string fileName, string language, CancellationToken token)
    {
        if (code.Length > 2_000_000 || string.IsNullOrWhiteSpace(code)) throw new InvalidDataException("OneC.InputLimit");
        var installed = runtime ?? new();
        if (executableForTest is null) await installed.PrepareAsync(false, null, token);
        var executable = executableForTest ?? installed.Executable;
        var directory = Path.Combine(AppDataPaths.BaseDirectory, "OneC", "Checks", OneCIds.New());
        var sourceDirectory = Path.Combine(directory, "source"); var reports = Path.Combine(directory, "reports");
        Directory.CreateDirectory(sourceDirectory); Directory.CreateDirectory(reports);
        var source = Path.Combine(sourceDirectory, fileName.EndsWith(".os", StringComparison.OrdinalIgnoreCase) ? "Module.os" : "Module.bsl");
        await File.WriteAllTextAsync(source, code, new UTF8Encoding(false), token);
        var config = Path.Combine(directory, "configuration.json");
        await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new { language = language == "en" ? "en" : "ru" }), token);
        try
        {
            var process = await OneCBslRuntime.RunAsync(executable,
                ["analyze", "--configuration", config, "--srcDir", sourceDirectory, "--outputDir", reports, "--reporter", "json", "--silent"],
                directory, token);
            var report = Path.Combine(reports, "bsl-json.json");
            if (process.ExitCode != 0 || !File.Exists(report) || new FileInfo(report).Length > 8_000_000)
                throw new InvalidDataException("OneC.CheckerFailed");
            var json = await File.ReadAllTextAsync(report, Encoding.UTF8, token);
            var diagnostics = Parse(json, source, code.Count(c => c == '\n') + 1);
            return new(OneCProjectStore.Hash(code), Id, OneCBslRuntime.Version, diagnostics.Length == 0 ? "clear" : "issues", diagnostics,
                DateTimeOffset.UtcNow, "static analysis; 1C platform not executed");
        }
        finally
        {
            // This generated directory contains only input snapshots and checker reports.
            var checksRoot = Path.GetFullPath(Path.Combine(AppDataPaths.BaseDirectory, "OneC", "Checks")) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(directory).StartsWith(checksRoot, StringComparison.OrdinalIgnoreCase))
            {
                try { Lopata.Updates.SafeUpdatePath.RejectLinks(directory); Directory.Delete(directory, true); }
                catch (IOException) { OwnedProcessRegistry.Log("onec_check_cleanup_deferred", "OneC"); }
            }
        }
    }
    internal static OneCDiagnostic[] Parse(string json, string source, int lineCount)
    {
        try
        {
            using var report = JsonDocument.Parse(json, new() { MaxDepth = 32 });
            var files = report.RootElement.GetProperty("fileinfos");
            if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() != 1) throw new InvalidDataException("OneC.CheckerFailed");
            var file = files[0];
            if (!Uri.TryCreate(file.GetProperty("path").GetString(), UriKind.Absolute, out var uri) || !uri.IsFile
                || !Path.GetFullPath(uri.LocalPath).Equals(Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("OneC.CheckerFailed");
            var items = file.GetProperty("diagnostics");
            if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 10000) throw new InvalidDataException("OneC.CheckerFailed");
            return items.EnumerateArray().Select(d =>
            {
                var range = d.GetProperty("range"); var start = range.GetProperty("start"); var end = range.GetProperty("end");
                var line = start.GetProperty("line").GetInt32(); var column = start.GetProperty("character").GetInt32();
                var endLine = end.GetProperty("line").GetInt32(); var endColumn = end.GetProperty("character").GetInt32();
                if (line < 0 || line > lineCount || endLine < line || endLine > lineCount || column < 0 || endColumn < 0)
                    throw new InvalidDataException("OneC.CheckerFailed");
                var message = d.GetProperty("message").GetString() ?? "";
                if (message.Length > 16000) throw new InvalidDataException("OneC.CheckerFailed");
                return new OneCDiagnostic(d.GetProperty("code").GetString() ?? "", d.GetProperty("severity").GetString() ?? "",
                    message, line + 1, column + 1, endLine + 1, endColumn + 1);
            }).ToArray();
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new InvalidDataException("OneC.CheckerFailed", error); }
    }
}
