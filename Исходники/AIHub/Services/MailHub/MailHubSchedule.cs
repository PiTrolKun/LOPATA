using Lopata.MailHub;

namespace AIHub.Services.MailHub;

internal static class MailHubSchedule
{
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan StopBeforeNext = TimeSpan.FromHours(3);

    internal static DateTimeOffset? Next(int[] hours, DateTimeOffset after, TimeZoneInfo zone) =>
        MailHubCalendar.Next(hours, after, zone);

    internal static MailHubPending? Due(MailHubSettings settings, MailHubState state,
        DateTimeOffset now, TimeZoneInfo zone)
    {
        if (state.Paused) return null;
        if (state.Pending is { } pending)
            return pending.RetryAt > now ? null : pending;
        int[] hours = settings.ScheduleEnabled ? settings.Hours : [];
        var after = state.ObservedSince ?? now;
        if (state.LastSlot is { } slot && slot > after) after = slot;
        var next = Next(hours, after, zone);
        if (next is { } due && due <= now) return new(due, due, Early: false);
        if (state.ManualRequest is { } request)
            return new(request, Next(hours, now, zone), Early: true);
        return null;
    }

    internal static DateTimeOffset Deadline(MailHubSettings settings, MailHubPending pending, TimeZoneInfo zone)
    {
        var next = settings.ScheduleEnabled
            ? pending.Early ? pending.Slot : Next(settings.Hours, pending.Slot ?? pending.Requested, zone)
            : null;
        return next?.Subtract(StopBeforeNext) ?? pending.Requested.Add(StopBeforeNext);
    }

    internal static MailHubState Finish(MailHubState state, MailHubPending pending, DateTimeOffset now,
        bool complete, string status, string[] unavailable)
    {
        var fulfilled = state.LastSlot;
        if ((!pending.Early || complete) && pending.Slot is { } slot && (fulfilled is null || slot > fulfilled))
            fulfilled = slot;
        var observed = pending.Early && !complete && pending.Slot is { } remaining && remaining <= now
            && (fulfilled is null || remaining > fulfilled) ? remaining.AddTicks(-1) : now;
        return state with
        {
            LastSlot = fulfilled, ObservedSince = observed, ManualRequest = null, Pending = null,
            Paused = false, Status = status, StatusDetail = null, StatusAt = now, OutcomeReport = null, UnavailableAccounts = unavailable
        };
    }
}
