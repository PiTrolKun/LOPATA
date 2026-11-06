namespace AIHub.Services.LivingArcade;

public enum LivingArcadeViewMode
{
    TopDown,
    Perspective
}

/// <summary>Reproducible presentation only; no changes to simulation or saved run data.</summary>
public static class LivingArcadePresentationPolicy
{
    public static LivingArcadeViewMode ModeFor(int runSeed, int levelNumber)
    {
        var zeroBasedLevel = Math.Max(1, levelNumber) - 1;
        var chapter = zeroBasedLevel / 3;
        var slot = zeroBasedLevel % 3;
        var state = Mix(unchecked((uint)runSeed ^ (uint)chapter * 0x9E3779B9u ^ 0xD1B54A35u));

        // Every chapter contains both views and one independently chosen extra view.
        Span<LivingArcadeViewMode> order = stackalloc LivingArcadeViewMode[3]
        {
            LivingArcadeViewMode.TopDown,
            LivingArcadeViewMode.Perspective,
            (state & 1) == 0 ? LivingArcadeViewMode.TopDown : LivingArcadeViewMode.Perspective
        };
        for (var index = order.Length - 1; index > 0; index--)
        {
            state = Mix(unchecked(state + (uint)index));
            var selected = (int)(state % (uint)(index + 1));
            (order[index], order[selected]) = (order[selected], order[index]);
        }
        return order[slot];
    }

    private static uint Mix(uint value)
    {
        unchecked
        {
            value ^= value >> 16;
            value *= 0x7FEB352Du;
            value ^= value >> 15;
            value *= 0x846CA68Bu;
            return value ^ (value >> 16);
        }
    }
}
