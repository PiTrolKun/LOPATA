using AIHub.Models;

namespace AIHub.Services.LivingArcade;

public sealed partial class LivingArcadeSimulation
{
    private Enemy? _boss;
    private bool _bossStarted;
    private double _bossMaximumHealth;

    private int ActiveBossPhase => _boss is { Removed: false } ? _boss.BossPhase : 0;
    private double ActiveBossHealth => _boss is { Removed: false } ? Math.Max(0, _boss.Health) : 0;
    private double ActiveBossMaximumHealth => _boss is { Removed: false } ? _bossMaximumHealth : 0;

    private bool TryStartBoss()
    {
        if (_bossStarted || _level.Number % 3 != 0 || _defeated
            || _progress < _level.DurationSeconds || _spawnIndex < _schedule.Count
            || _eventIndex < _events.Length || _enemies.Count != 0) return false;

        _bossStarted = true;
        _spawnBudget--;
        // Separate the boss's telegraphed opening from old off-screen enemy projectiles.
        _shots.RemoveAll(shot => shot.Hostile);
        var definition = new LivingArcadeEnemyDefinition
        {
            Id = "stage_boss", TitleRu = _bossArt?.TitleRu ?? "Хранитель дороги", TitleEn = _bossArt?.TitleEn ?? "Road guardian",
            Health = Math.Min(240, 70 + _level.Number * 4d), Radius = .12,
            Color = "#FFAA55", Shape = "star", EncounterClass = "rare-large", Visual = _bossArt?.Visual
        };
        _bossMaximumHealth = definition.Health * _difficulty;
        _boss = new()
        {
            Id = "stage_boss-" + ++_serial, Definition = definition, IsBoss = true,
            ExperienceValue = 30,
            BossPhase = 1, State = definition.InitialState, X = .5, Y = .23,
            Health = _bossMaximumHealth, FireClock = 1.5, Telegraph = true
        };
        // The boss is authoritative runtime content, separate from the model's bounded waves.
        // It starts after ordinary combat, and shares the same global projectile limit.
        _enemies.Add(_boss);
        Sound?.Invoke("boss");
        return true;
    }

    private void UpdateBoss(Enemy boss, double seconds)
    {
        boss.Age += seconds;
        boss.StateTime += seconds;
        boss.Flash = Math.Max(0, boss.Flash - seconds);
        var fraction = boss.Health / _bossMaximumHealth;
        var phase = fraction > 2d / 3 ? 1 : fraction > 1d / 3 ? 2 : 3;
        if (phase > boss.BossPhase)
        {
            boss.BossPhase = phase;
            boss.StateTime = 0;
            boss.FireClock = 1;
            Sound?.Invoke("boss_phase");
        }
        boss.ShieldMode = "off";
        boss.Telegraph = boss.StateTime < 1;
        var frequency = boss.BossPhase switch { 1 => .55, 2 => .8, _ => 1.05 };
        var width = boss.BossPhase switch { 1 => .2, 2 => .26, _ => .3 };
        boss.X = .5 + Math.Sin(boss.Age * frequency) * width;
        boss.Y = .24 + Math.Sin(boss.Age * .45) * .025;
        boss.FireClock = Math.Max(0, boss.FireClock - seconds);
        if (boss.Telegraph || boss.FireClock > 0) return;

        var originY = boss.Y + boss.Definition.Radius * .6;
        var aim = Math.Atan2(_x - boss.X, .86 - originY);
        switch (boss.BossPhase)
        {
            case 1:
                BossVolley(boss, originY, aim, 3, .22, .27);
                boss.FireClock = 1.4;
                break;
            case 2:
                // Two narrow fans leave a visible gap between their prongs.
                BossVolley(boss, originY, aim - .26, 2, .12, .3);
                BossVolley(boss, originY, aim + .26, 2, .12, .3);
                boss.FireClock = 1.15;
                break;
            default:
                // The final fan sweeps across the road instead of tracking every dodge.
                BossVolley(boss, originY, Math.Sin(boss.Age * 1.3) * .25, 7, 1.25, .31);
                boss.FireClock = .95;
                break;
        }
    }

    private void BossVolley(Enemy boss, double y, double angle, int count, double spread, double speed)
    {
        for (var index = 0; index < count; index++)
            AddShot(boss.X, y, angle + (index / (double)(count - 1) - .5) * spread,
                speed, true, 9 * _difficulty);
    }
}
