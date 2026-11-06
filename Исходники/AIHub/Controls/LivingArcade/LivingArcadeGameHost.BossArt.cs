using AIHub.Models;
using AIHub.Services.LivingArcade;

namespace AIHub.Controls.LivingArcade;

public sealed partial class LivingArcadeGameHost
{
    private LivingArcadeLibrary? _bossLibrary;

    public bool HasGeneratedBossArt => _simulation?.BossArt?.Source == "model";

    private void PrepareSprites(LivingArcadeSimulation simulation)
    {
        if (simulation.Snapshot.GameLevel % 3 == 0 && simulation.BossArt is null)
        {
            _bossLibrary ??= LivingArcadeLibrary.Load();
            simulation.SetBossArt(LivingArcadeBossArtCatalog.CreateFallback(simulation.LevelDefinition, _bossLibrary));
        }
        _sprites.Prepare(simulation.EnemyDefinitions);
    }

    /// <summary>Prepares a completed illustration and swaps it without restarting or pausing combat.</summary>
    public void SetBossArt(LivingArcadeBossArt art)
    {
        if (_simulation is not { } simulation || simulation.Snapshot.GameLevel % 3 != 0) return;
        LivingArcadeBossArtPolicy.Validate(art);
        // Preparation is atomic inside the sprite cache. Keep the previous drawing if loading fails.
        _sprites.Prepare(simulation.EnemyDefinitions.Where(enemy => enemy.Id != "stage_boss_art")
            .Append(new LivingArcadeEnemyDefinition { Id = "stage_boss_art", Visual = art.Visual }));
        simulation.SetBossArt(art);
        _graphicsReleased = false;
        InvalidateVisual();
    }
}
