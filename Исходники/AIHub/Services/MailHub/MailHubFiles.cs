using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Lopata.MailHub;

internal sealed record MailHubReport(Guid Id, DateTimeOffset Created, bool Listened = false);
internal sealed record MailHubCursor(uint Validity, uint Uid);
internal sealed record MailHubPending(DateTimeOffset Requested, DateTimeOffset? Slot, bool Early,
    DateTimeOffset? RetryAt = null, string? DelayReason = null, string? OperationId = null);
internal sealed record MailHubState
{
    public MailHubReport[] Reports { get; init; } = [];
    public Dictionary<string, MailHubCursor> Cursors { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<Guid, ulong[]> GmailMessages { get; init; } = [];
    public DateTimeOffset? ManualRequest { get; init; }
    public MailHubPending? Pending { get; init; }
    public DateTimeOffset? LastSlot { get; init; }
    public DateTimeOffset? ObservedSince { get; init; }
    public bool Paused { get; init; }
    public string Status { get; init; } = "";
    public string? StatusDetail { get; init; }
    public string[] UnavailableAccounts { get; init; } = [];
    public Guid[] ReconnectAccounts { get; init; } = [];
    public DateTimeOffset? StatusAt { get; init; }
    public Guid? OutcomeReport { get; init; }

}

/// <summary>Local, durable exchange between the main app and the island. Credentials and message text stay outside it.</summary>
internal sealed class MailHubFiles
{
    private readonly string _name;
    private readonly string _state;
    internal string Root { get; }
    internal string Reports { get; }
    internal MailHubFiles(string profile)
    {
        Root = Path.Combine(profile, "MailHub");
        Reports = Path.Combine(Root, "reports");
        _state = Path.Combine(Root, "state.json");
        _name = @"Local\LOPATA.MailHub." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Environment.UserDomainName + "\\" + Environment.UserName + "|" + Path.GetFullPath(Root).ToUpperInvariant())))[..32];
    }

    internal MailHubState Load() => ReadJson<MailHubState>(_state) ?? new();
    internal string ReportPath(Guid id) => Path.Combine(Reports, id.ToString("N") + ".wav");
    internal Mutex ProcessMarker(bool island) => new(false, MarkerName(island));
    internal bool IsProcessOpen(bool island)
    {
        if (!Mutex.TryOpenExisting(MarkerName(island), out var marker)) return false;
        marker.Dispose();
        return true;
    }
    private string MarkerName(bool island) => _name + (island ? ".Island" : ".Host");

    internal bool TryRequest(bool island, DateTimeOffset now)
    {
        if (ReadDisplaySettings() is not { Accounts.Length: > 0 } settings || settings.BindToIsland != island
            || !IsProcessOpen(island)) return false;
        Update(state => state.Pending is not null || state.ManualRequest is not null ? state
            : state with { ManualRequest = now, Paused = false });
        return true;
    }

    internal FileStream? TryBeginCycle()
    {
        Directory.CreateDirectory(Root);
        try { return new FileStream(Path.Combine(Root, "cycle.lock"), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None); }
        catch (IOException) { return null; }
    }

    internal MailHubState Update(Func<MailHubState, MailHubState> apply)
    {
        using var guard = new Mutex(false, _name + ".State");
        try
        {
            if (!guard.WaitOne(TimeSpan.FromSeconds(3))) throw new IOException("Mail hub state is busy.");
        }
        catch (AbandonedMutexException) { }
        try
        {
            var before = Load();
            var after = apply(before);
            if (!ReferenceEquals(before, after)) WriteJson(_state, after);
            return after;
        }
        finally { guard.ReleaseMutex(); }
    }

    internal bool BindToIsland() => ReadDisplaySettings()?.BindToIsland == true;
    internal bool HasAccounts() => ReadDisplaySettings()?.Accounts?.Length > 0;

    internal (DateTimeOffset? At, bool Retry) NextCheck(MailHubState state, DateTimeOffset now, TimeZoneInfo zone)
    {
        var settings = ReadDisplaySettings();
        if (settings?.Accounts is not { Length: > 0 } || state.Paused) return (null, false);
        if (state.Pending?.RetryAt is { } retry) return (retry, true);
        if (!settings.ScheduleEnabled) return (null, false);
        var after = state.LastSlot is { } fulfilled && fulfilled > now ? fulfilled : now;
        return (MailHubCalendar.Next(settings.Hours ?? [], after, zone), false);
    }

    private MailHubDisplaySettings? ReadDisplaySettings()
    {
        try { return ReadJson<MailHubDisplaySettings>(Path.Combine(Root, "settings.json")); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    internal void MarkListened(Guid id) => Update(state => state.Reports.Any(report => report.Id == id && !report.Listened)
        ? state with { Reports = state.Reports.Select(report => report.Id == id ? report with { Listened = true } : report).ToArray() }
        : state);

    internal void Delete(Guid id) => Update(state =>
    {
        if (state.Reports.All(report => report.Id != id)) return state;
        File.Delete(ReportPath(id));
        return state with { Reports = state.Reports.Where(report => report.Id != id).ToArray() };
    });

    internal void CopyReport(Guid id, string destination)
    {
        var source = ReportPath(id);
        if (string.Equals(Path.GetFullPath(destination), source, StringComparison.OrdinalIgnoreCase)) return;
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    internal bool IsReady => SafeLatest() is { Listened: false };

    internal MailHubReport? SafeLatest(Guid? playingId = null)
    {
        try { return Load().Reports.Where(report => File.Exists(ReportPath(report.Id)))
            .OrderByDescending(report => report.Id == playingId).ThenBy(report => report.Listened)
            .ThenByDescending(report => report.Created).FirstOrDefault(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    internal static T? ReadJson<T>(string path) where T : class
    {
        try
        {
            using var data = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<T>(data);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    internal static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private sealed record MailHubDisplaySettings(bool BindToIsland, JsonElement[]? Accounts, bool ScheduleEnabled, int[]? Hours);
}

internal static class MailHubCalendar
{
    internal static DateTimeOffset? Next(int[] hours, DateTimeOffset after, TimeZoneInfo zone)
    {
        var selected = hours.Where(hour => hour is >= 0 and < 168).Distinct().Order().ToArray();
        if (selected.Length == 0) return null;
        var local = TimeZoneInfo.ConvertTime(after, zone).Date;
        for (var day = local; day <= local.AddDays(14); day = day.AddDays(1))
        {
            var weekday = ((int)day.DayOfWeek + 6) % 7;
            foreach (var slot in selected)
            {
                if (slot / 24 != weekday) continue;
                var time = DateTime.SpecifyKind(day.AddHours(slot % 24), DateTimeKind.Unspecified);
                if (zone.IsInvalidTime(time)) continue;
                var offset = zone.IsAmbiguousTime(time) ? zone.GetAmbiguousTimeOffsets(time).Max() : zone.GetUtcOffset(time);
                var occurrence = new DateTimeOffset(time, offset);
                if (occurrence > after) return occurrence;
            }
        }
        return null;
    }
}
