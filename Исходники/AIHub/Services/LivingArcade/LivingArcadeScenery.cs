namespace AIHub.Services.LivingArcade;

/// <summary>A presentation-only scene. It never changes the saved model plan or collision geometry.</summary>
public sealed record LivingArcadeScenery(string Kind, int Seed, int Variant, int Chapter);

/// <summary>One location per three-stage boss chapter, with a repeatable, different successor.</summary>
public static class LivingArcadeSceneryPolicy
{
    public static IReadOnlyList<string> Kinds { get; } = Array.AsReadOnly(new[]
        { "harbor", "forest", "desert", "ice", "city", "alien" });

    public static LivingArcadeScenery Create(int runSeed, int levelNumber)
    {
        var number = Math.Max(1, levelNumber);
        var chapter = (number - 1) / 3 + 1;
        var order = Enumerable.Range(0, Kinds.Count).ToArray();
        var state = Mix(unchecked((uint)runSeed) ^ 0xA341316Cu);
        for (var index = order.Length - 1; index > 0; index--)
        {
            state = Mix(state + (uint)index);
            var selected = (int)(state % (uint)(index + 1));
            (order[index], order[selected]) = (order[selected], order[index]);
        }
        // A shuffled cycle visits every setting and cannot repeat at the next boss boundary.
        var kind = Kinds[order[(chapter - 1) % order.Length]];
        var scenerySeed = unchecked((int)Mix((uint)runSeed ^ (uint)chapter * 0x9E3779B9u));
        return new(kind, scenerySeed, (number - 1) % 3, chapter);
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
