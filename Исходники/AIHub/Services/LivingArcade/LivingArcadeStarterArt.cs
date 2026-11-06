using AIHub.Models;

namespace AIHub.Services.LivingArcade;

/// <summary>Only newly created authored first levels gain library clothes; saved runs and their mechanics are unchanged.</summary>
internal static class LivingArcadeStarterArt
{
    internal static void Apply(LivingArcadeLevel level, LivingArcadeLibrary library)
    {
        var choices = library.Shortlist("robot creature animal character", level.Seed, level.EnemyTypes.Count);
        for(var i=0;i<choices.Count;i++)
            level.EnemyTypes[i].Visual = new LivingArcadeVisual { Mode="library", AssetId=choices[i].Id,
                Animation=choices[i].Frames.Length>1 ? "cycle" : "bob",AnimationAmount=2,AnimationPeriod=1.5 };
        LivingArcadeGeneration.RecordArtSources(level,library);
    }
}
