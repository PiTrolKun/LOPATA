using Lopata.Updates;

namespace AIHub.Services;

public static class AutomaticUpdateSchedule
{
    public static TimeSpan? Interval(bool enabled, bool hiddenInTray, UpdateDelivery? direction)
        => !enabled || direction is null ? null
        : hiddenInTray ? TimeSpan.FromMinutes(10)
        : direction == UpdateDelivery.FilePatch ? TimeSpan.FromMinutes(30) : null;
}
