using System.Text.Json;
using AIHub.Services.ExternalAgents;

namespace AIHub.Tests;

[TestClass]
public sealed class AgentDiagnosticsTests
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "AIHubTests", Guid.NewGuid().ToString("N"));
    private readonly AgentDiagnosticsSources _sources = new();
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 13, 0, 0, TimeSpan.Zero);

    [TestInitialize]
    public void SetUp() => Directory.CreateDirectory(_folder);

    [TestCleanup]
    public void CleanUp() => Directory.Delete(_folder, recursive: true);

    [TestMethod]
    public void FailedInstallationAndRetryRemainTraceableWithoutExposingPrivateData()
    {
        const string marker = "unique-private-text";
        const string id = "runtime.onec.bsl-ls";
        var secret = @"C:\Users\private-person\document.docx token=sk-private " + marker;
        Write("components", "components-2026-01.jsonl",
            new { timestamp = Start, @event = "component_download_started", data = new { id, downloadUrl = secret } },
            new { timestamp = Start.AddSeconds(1), @event = "component_install_failed", data = new {
                id, message = $"Not enough free space for '{secret}'.", errorType = "System.IO.IOException", prompt = marker } },
            new { timestamp = Start.AddSeconds(2), @event = "component_verified", data = new { id = "builtin.dotnet", status = "installed" } },
            new { timestamp = Start.AddSeconds(3), @event = "component_verified", data = new { id, status = "installed", installPath = secret } });
        var found = Search(new { source = "components", operation = "install", component = id });
        var error = found.GetProperty("events").EnumerateArray().Single();
        var response = Read(error.GetProperty("id").GetString()!);
        Assert.AreEqual("found", response.GetProperty("status").GetString());
        Assert.AreEqual("insufficient_disk_space", error.GetProperty("technicalReason").GetString());
        Assert.AreEqual("IOException", error.GetProperty("errorType").GetString());
        Assert.AreEqual("component_download_started", response.GetProperty("before")[0].GetProperty("operation").GetString());
        var retry = response.GetProperty("after").EnumerateArray().Single();
        Assert.AreEqual("completed", retry.GetProperty("outcome").GetString());
        Assert.AreEqual(id, retry.GetProperty("component").GetString());
        foreach (var projection in new[] { found, response })
            foreach (var value in new[] { "private-person", "sk-private", marker, "document.docx" })
                Assert.IsFalse(projection.ToString().Contains(value, StringComparison.Ordinal));
    }

    [TestMethod]
    public void ArchivedExitShowsOnlyObservedCodeAndDoesNotExplainANewerMissingAttempt()
    {
        var path = Write("recovery", "events.jsonl",
            new { at = Start, action = "unexpected_exit", pid = 445, detail = -1 });
        var original = Search(new { source = "recovery" }).GetProperty("events")[0];
        File.Move(path, path + ".previous");
        _sources.Add("recovery", _folder, "events.jsonl.previous");
        File.WriteAllText(path, "{ incomplete");
        var read = Read(original.GetProperty("id").GetString()!);
        var selected = read.GetProperty("selected");
        Assert.AreEqual(-1, selected.GetProperty("exitCode").GetInt32());
        Assert.AreEqual(JsonValueKind.Null, selected.GetProperty("technicalReason").ValueKind);
        Assert.AreEqual(Start, selected.GetProperty("time").GetDateTimeOffset());
        var recent = Search(new { source = "recovery", from = Start.AddDays(1).ToString("O") });
        Assert.AreEqual("no_matching_records", recent.GetProperty("status").GetString());
        Assert.AreEqual(0, recent.GetProperty("events").GetArrayLength());
        Assert.AreEqual(1, recent.GetProperty("coverage").GetProperty("unreadableRecords").GetInt32());
    }

    [TestMethod]
    public void ScenarioSessionFiltersUseOpaqueIdsAndNeverForwardPrompts()
    {
        Write("uncertainty", "core.jsonl",
            new { Type = "executor_download_error", SessionId = "private-name", UtcTime = Start, Payload = new {
                Message = @"C:\private-person\notes.txt with prompt contents", ErrorType = "System.Net.Http.HttpRequestException" } },
            new { Type = "executor_download_completed", SessionId = "private-name", UtcTime = Start.AddSeconds(1), Payload = "private prompt" },
            new { Type = "executor_download_error", SessionId = "another-private-name", UtcTime = Start.AddSeconds(2), Payload = new { } });
        var matches = Search(new { scenario = "uncertainty", until = Start.AddSeconds(1).ToString("O") }).GetProperty("events");
        var failed = matches.EnumerateArray().Single();
        var session = failed.GetProperty("session").GetString();
        Assert.AreEqual("HttpRequestException", failed.GetProperty("errorType").GetString());
        Assert.IsFalse(session!.Contains("private-name", StringComparison.Ordinal));
        var filtered = Search(new { session, includeInfo = true });
        Assert.AreEqual(2, filtered.GetProperty("events").GetArrayLength());
        var read = Read(failed.GetProperty("id").GetString()!);
        Assert.AreEqual("executor_download_completed", read.GetProperty("after").EnumerateArray().Single().GetProperty("operation").GetString());
        Assert.IsFalse(read.ToString().Contains("private", StringComparison.Ordinal));
    }

    [TestMethod]
    public void MissingSourceIsReadOnlyAndAnArbitraryPathIsNotAnEventId()
    {
        var missing = Path.Combine(_folder, "no-source");
        _sources.Add("recovery", missing, "events.jsonl");
        Assert.AreEqual("logs_unavailable", Search(new { source = "recovery" }).GetProperty("status").GetString());
        Assert.IsFalse(Directory.Exists(missing));
        Assert.ThrowsExactly<ArgumentException>(() => Read(Path.Combine(_folder, "arbitrary.jsonl")));
    }

    [TestMethod]
    public void LiteraryStartupUsesRealStageAndExitCodeWithoutContentsOfRequestOrStderr()
    {
        const string secret = "user-secret-in-stdout";
        Write("literary", "request.jsonl",
            new { utc = Start, kind = "request", data = new { body = secret } },
            new { utc = Start.AddSeconds(1), kind = "startup_failure", data = new
            {
                stage = "health_check", data = new { reason = "process_exited", process = new { ExitCode = 42 },
                    exception = "System.IO.IOException: " + secret, tail = new[] { secret } }
            } });
        var failed = Search(new { source = "literary" }).GetProperty("events").EnumerateArray().Single();
        Assert.AreEqual("health_check", failed.GetProperty("stage").GetString());
        Assert.AreEqual(42, failed.GetProperty("exitCode").GetInt32());
        Assert.AreEqual("process_exited", failed.GetProperty("technicalReason").GetString());
        Assert.IsFalse(Read(failed.GetProperty("id").GetString()!).ToString().Contains(secret, StringComparison.Ordinal));
    }

    [TestMethod]
    public void HistoricalReadFindsAnOlderSessionAmongManyNewerOnes()
    {
        _sources.Add("core", _folder, "*.jsonl");
        for (var day = 0; day < 70; day++)
        {
            var stamp = Start.AddDays(day);
            var path = Path.Combine(_folder, $"{day:D2}.jsonl");
            File.WriteAllText(path, JsonSerializer.Serialize(new { type = "catalog_startup_sync_error", utcTime = stamp, sessionId = day.ToString() }));
            File.SetLastWriteTimeUtc(path, stamp.UtcDateTime);
        }
        var result = Search(new { source = "core", until = Start.ToString("O") });
        var selected = result.GetProperty("events").EnumerateArray().Single();
        Assert.AreEqual(Start, Read(selected.GetProperty("id").GetString()!).GetProperty("selected").GetProperty("time").GetDateTimeOffset());
    }

    [TestMethod]
    public void HistoricalReadSelectsAWindowInsideALargeJournal()
    {
        var path = Path.Combine(_folder, "archive.jsonl");
        using (var writer = File.CreateText(path))
        {
            var padding = new string('x', 8192);
            for (var minute = 0; minute < 550; minute++)
                writer.WriteLine(JsonSerializer.Serialize(new { type = "catalog_startup_sync_error", utcTime = Start.AddMinutes(minute), payload = padding }));
        }
        _sources.Add("core", _folder, "archive.jsonl");
        var target = Start.AddMinutes(17);
        var result = Search(new { source = "core", from = target.ToString("O"), until = target.ToString("O") });
        var selected = result.GetProperty("events").EnumerateArray().Single();
        Assert.AreEqual(target, Read(selected.GetProperty("id").GetString()!).GetProperty("selected").GetProperty("time").GetDateTimeOffset());
    }

    private string Write(string source, string name, params object[] rows)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllLines(path, rows.Select(row => JsonSerializer.Serialize(row)));
        _sources.Add(source, _folder, name);
        return path;
    }

    private JsonElement Search(object options) =>
        JsonSerializer.SerializeToElement(AgentDiagnostics.Search(JsonSerializer.SerializeToElement(options), _sources));

    private JsonElement Read(string eventId) =>
        JsonSerializer.SerializeToElement(AgentDiagnostics.Read(JsonSerializer.SerializeToElement(new { eventId }), _sources));
}
