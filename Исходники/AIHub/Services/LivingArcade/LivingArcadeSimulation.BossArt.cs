using AIHub.Models;

namespace AIHub.Services.LivingArcade;

public sealed partial class LivingArcadeSimulation
{
    private LivingArcadeBossArt? _bossArt;
    private IReadOnlyList<LivingArcadeEnemyDefinition>? _visualEnemyDefinitions;

    public LivingArcadeBossArt? BossArt => _bossArt;
    internal LivingArcadeLevel LevelDefinition => _level;

    /// <summary>Changes only illustration data, including during an already active boss fight.</summary>
    public void SetBossArt(LivingArcadeBossArt art)
    {
        ArgumentNullException.ThrowIfNull(art);
        if (_level.Number % 3 != 0) throw new InvalidOperationException("This stage has no final boss.");
        LivingArcadeBossArtPolicy.Validate(art);
        _bossArt = art;
        _visualEnemyDefinitions = [.. _level.EnemyTypes,
            new LivingArcadeEnemyDefinition { Id = "stage_boss_art", Visual = art.Visual }];
        if (_boss is not null)
        {
            _boss.Definition.Visual = art.Visual;
            _boss.Definition.TitleRu = art.TitleRu;
            _boss.Definition.TitleEn = art.TitleEn;
        }
    }
}
