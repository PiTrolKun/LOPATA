namespace AIHub.Services.LivingArcade;

public sealed partial class LivingArcadeSimulation
{
    private string CurrentWeapon => _powerup switch
    {
        "weapon_laser" => "laser",
        "weapon_rocket" => "rockets",
        _ => _hero.WeaponKind
    };
    private double CurrentWeaponSeconds => _powerup is "weapon_laser" or "weapon_rocket" ? _powerupSeconds : 0;
    private double WeaponRateMultiplier => CurrentWeapon switch
    { "shotgun" => .5, "smg" => 1.8, "laser" => 1.6, "rockets" => .4, _ => 1 };
    private double WeaponDamageMultiplier => CurrentWeapon switch
    { "shotgun" => .6, "smg" => .5, "laser" => .8, "rockets" => 3, _ => 1 };
    private int WeaponShotCount => CurrentWeapon == "shotgun" ? 3 : 1;

    private void ShootPlayerVolley()
    {
        var weapon = CurrentWeapon;
        var speed = weapon switch { "laser" => 1.5, "rockets" => .52, "shotgun" => .72, _ => .78 };
        var spread = weapon == "shotgun" ? .13 : .08;
        for (var index = 0; index < SpreadShots; index++)
        {
            if (_shots.Count >= 512) break;
            var angle = _angle + (index - (SpreadShots - 1) / 2d) * spread;
            var shot = new Shot
            {
                X = _x, Y = .82, Vx = Math.Sin(angle) * speed, Vy = -Math.Cos(angle) * speed,
                Damage = ProjectileDamage, Speed = speed, OriginX = _x, OriginY = .82,
                Kind = weapon, Pierces = weapon == "laser" ? 3 : 0,
                SplashRadius = weapon == "rockets" ? .07 : 0
            };
            ConfigurePlayerShot(shot);
            _shots.Add(shot);
        }
    }

    // The optional host reward implementation can change a finite projectile pattern.
    partial void ConfigurePlayerShot(Shot shot);

    private void ApplyPlayerShotHit(Shot shot, Enemy enemy, bool blocked)
    {
        if (!shot.HitEnemies.Add(enemy)) return;
        enemy.Flash = .08;
        if (blocked) { shot.Spent = true; return; }
        enemy.Health -= shot.Damage;
        if (enemy.Health <= 0) Kill(enemy);
        if (shot.SplashRadius > 0)
        {
            // At most three secondary victims, with shields still respected.
            foreach (var victim in _enemies.Where(e => !shot.HitEnemies.Contains(e)
                && Distance(e.X, e.Y, shot.X, shot.Y) <= shot.SplashRadius + e.Definition.Radius)
                .OrderBy(e => Distance(e.X, e.Y, shot.X, shot.Y)).Take(3).ToArray())
            {
                shot.HitEnemies.Add(victim);
                var angle = Math.Atan2(shot.X - victim.X, victim.Y - shot.Y) * 180 / Math.PI;
                if (victim.ShieldMode == "full" || victim.ShieldMode == "rotating"
                    && Math.Abs(NormalizeDegrees(angle - victim.WeakSpotAngle)) > victim.WeakSpotWidth / 2) continue;
                victim.Health -= shot.Damage * .65; victim.Flash = .12;
                if (victim.Health <= 0) Kill(victim);
            }
            Sound?.Invoke("hit");
        }
        if (shot.Pierces > 0)
        {
            shot.Pierces--;
            shot.Damage *= .8;
        }
        else shot.Spent = true;
    }
}
