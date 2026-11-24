using System.Globalization;
using System.IO;
using System.Text.Json;

namespace AIHub.Services.ExternalAgents;

/// <summary>Offline, read-only diagnosis. Only known sources and projected technical fields leave this boundary.</summary>
public static class AgentDiagnostics
{
    public static IReadOnlyList<string> Sources { get; } = Array.AsReadOnly(AgentDiagnosticsSources.Names);
    public static object Search(JsonElement args) => Search(args, AgentDiagnosticsSources.Local());
    public static object Read(JsonElement args) => Read(args, AgentDiagnosticsSources.Local());

    internal static object Search(JsonElement args, AgentDiagnosticsSources sources)
    {
        var source = Text(args, "source");
        if (source != null && !AgentDiagnosticsSources.Names.Contains(source))
            throw new ArgumentException("Unknown diagnostic source.");
        var operation = Text(args, "operation");
        var component = Text(args, "component");
        var session = Text(args, "session");
        var scenario = Text(args, "scenario");
        var from = Time(args, "from");
        var until = Time(args, "until");
        if (until < from) throw new ArgumentException("until must be at or after from.");
        var limit = 10;
        if (args.TryGetProperty("limit", out var limitValue) &&
            (limitValue.ValueKind != JsonValueKind.Number || !limitValue.TryGetInt32(out limit) || limit is < 1 or > 50))
            throw new ArgumentException("limit must be between 1 and 50.");
        var includeInfo = false;
        if (args.TryGetProperty("includeInfo", out var info))
        {
            if (info.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ArgumentException("includeInfo must be a boolean.");
            includeInfo = info.GetBoolean();
        }

        var snapshot = sources.Scan(source, until);
        var matches = snapshot.Events.Where(record =>
            (includeInfo || record.Outcome is "failed" or "cancelled" or "interrupted") &&
            (operation == null || record.Operation.Contains(operation, StringComparison.OrdinalIgnoreCase)) &&
            (scenario == null || string.Equals(scenario, record.Scenario, StringComparison.OrdinalIgnoreCase)) &&
            (component == null || string.Equals(component, record.Component, StringComparison.OrdinalIgnoreCase)) &&
            (session == null || session == record.Session) &&
            (from == null || record.Time >= from) && (until == null || record.Time <= until))
            .OrderByDescending(record => record.Time).ThenBy(record => record.Id).Take(limit + 1).ToArray();
        return new
        {
            status = matches.Length > 0 ? "found" : snapshot.FilesRead == 0 ? "logs_unavailable" : "no_matching_records",
            events = matches.Take(limit).Select(record => record.View()).ToArray(),
            moreMatches = matches.Length > limit,
            coverage = snapshot.Coverage()
        };
    }

    internal static object Read(JsonElement args, AgentDiagnosticsSources sources)
    {
        var id = Text(args, "eventId") ?? throw new ArgumentException("eventId from a previous search is required.");
        var parts = id.Split(':');
        if (parts.Length != 4 || !AgentDiagnosticsSources.Names.Contains(parts[0]) ||
            parts[1].Length != 32 || !parts[1].All(char.IsAsciiHexDigit) ||
            !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var position) ||
            parts[3].Length != 32 || !parts[3].All(char.IsAsciiHexDigit))
            throw new ArgumentException("Invalid eventId. Select an event returned by search.");

        var snapshot = sources.Scan(parts[0], selectedFile: parts[1], position: position);
        var selected = snapshot.Events.Find(record => record.Id == id);
        if (selected == null)
            return new { status = snapshot.FilesRead == 0 ? "logs_unavailable" : "event_not_found", coverage = snapshot.Coverage() };
        var processJournal = selected.Source is "recovery" or "processes";
        var related = snapshot.Events.Where(other => selected.Group != null && other.Group == selected.Group &&
            other.Id != selected.Id && (!processJournal || (selected.Time - other.Time).Duration() <= TimeSpan.FromMinutes(30)))
            .OrderBy(other => other.Time).ThenBy(other => other.Id).ToArray();
        var before = related.Where(other => other.Time < selected.Time).TakeLast(4);
        var after = related.Where(other => other.Time >= selected.Time).Take(4);
        return new
        {
            status = "found",
            selected = selected.View(),
            relation = selected.Relation,
            before = before.Select(record => record.View()).ToArray(),
            after = after.Select(record => record.View()).ToArray(),
            coverage = snapshot.Coverage()
        };
    }

    private static string? Text(JsonElement args, string key)
    {
        if (args.ValueKind != JsonValueKind.Object) throw new ArgumentException("Expected an object.");
        if (!args.TryGetProperty(key, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String || value.GetString()?.Trim() is not { Length: > 0 and <= 150 } text)
            throw new ArgumentException($"Invalid {key}.");
        return text;
    }

    private static DateTimeOffset? Time(JsonElement args, string key)
    {
        if (Text(args, key) is not { } text) return null;
        if ((!text.EndsWith('Z') && !(text.Length >= 6 && text[^6] is '+' or '-' && text[^3] == ':')) ||
            !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp))
            throw new ArgumentException($"{key} must be an ISO timestamp with a time zone.");
        return timestamp;
    }
}

internal sealed class AgentDiagnosticSnapshot
{
    internal List<AgentDiagnosticEvent> Events { get; } = [];
    internal int FilesRead { get; set; }
    internal HashSet<string> Limited { get; } = [];
    internal HashSet<string> Unavailable { get; } = [];
    internal int InvalidRecords { get; set; }
    internal object Coverage() => new
    {
        filesRead = FilesRead,
        latestRecognizedEventAt = Events.Count == 0 ? (DateTimeOffset?)null : Events.Max(record => record.Time),
        limitedSources = Limited.Order().ToArray(),
        unavailableSources = Unavailable.Order().ToArray(),
        unreadableRecords = InvalidRecords
    };
}

internal sealed record AgentDiagnosticEvent(string Id, DateTimeOffset Time, string Source,
    string Operation, string Stage, string Outcome, string? Scenario, string? Component,
    string? Session, int? ExitCode, string? ErrorType, string? TechnicalReason, string? Group, string? Relation)
{
    internal object View() => new
    {
        id = Id, time = Time, source = Source, operation = Operation, stage = Stage, outcome = Outcome,
        scope = Source == "recovery" ? "installation" : "application_log_store",
        scenario = Scenario, component = Component, session = Session, exitCode = ExitCode,
        errorType = ErrorType, technicalReason = TechnicalReason
    };
}
