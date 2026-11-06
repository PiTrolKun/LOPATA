using AIHub.Models;

namespace AIHub.Services.LivingArcade;

/// <summary>Supplements sparse saved plans without rewriting a model's accepted level or attempt.</summary>
public static class LivingArcadePickupSchedule
{
    public static LivingArcadeLevel ForPlay(LivingArcadeLevel level)
    {
        var events = level.MiniEvents.Select(e => e with { }).ToList();
        var state = unchecked((uint)level.Seed ^ (uint)level.Number * 0x9E3779B9u);
        if (state == 0) state = 17;
        double NextX()
        {
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            return .15 + state / (double)uint.MaxValue * .7;
        }
        // Up to nine pickups sample the varied effect and weapon bag during a normal stage.
        var target = Math.Min(9, (int)(level.DurationSeconds / 12));
        while (events.Count < 32 && events.Count(e => e.Kind == "bonus") < target)
        {
            var points = events.Where(e => e.Kind == "bonus").Select(e => e.At)
                .Append(0).Append(level.DurationSeconds - 12).OrderBy(x => x).ToArray();
            var gap = points.Zip(points.Skip(1), (a, b) => (Start: a, Length: b - a))
                .OrderByDescending(x => x.Length).First();
            if (gap.Length < 8) break;
            events.Add(new() { At = gap.Start + gap.Length / 2, X = NextX() });
        }
        // First stage remains educational. Later threats are visible, sparse and escapable.
        var negativeTarget = level.Number < 2 ? 0 : Math.Min(4, 2 + (level.Number - 2) / 4);
        for (var i = events.Count(e => e.Kind == "negative"); i < negativeTarget && events.Count < 32; i++)
            events.Add(new() { Kind = "negative", At = 12 + (level.DurationSeconds - 25) * (i + 1d) / (negativeTarget + 1), X = NextX() });
        // One helper pickup on about one in twenty stages, never part of the ordinary shuffled bag.
        var rareSeed = unchecked((uint)level.Seed * 0x85EBCA6Bu ^ (uint)level.Number * 0x9E3779B9u);
        if (level.Number >= 2 && rareSeed % 20 == 0 && !events.Any(e => e.BonusKind == "helper"))
        {
            var replacement = events.Where(e => e.Kind == "bonus" && !e.Tutorial && e.BonusKind == "auto")
                .OrderBy(e => Math.Abs(e.At - level.DurationSeconds * .6)).FirstOrDefault();
            if (replacement is not null) replacement.BonusKind = "helper";
        }
        var result = level with { MiniEvents = events.OrderBy(e => e.At).ToList() };
        LivingArcadeLevelPolicy.Validate(result);
        return result;
    }
}
