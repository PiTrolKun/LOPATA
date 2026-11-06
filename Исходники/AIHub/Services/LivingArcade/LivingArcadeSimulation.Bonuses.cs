using AIHub.Models;

namespace AIHub.Services.LivingArcade;

public sealed partial class LivingArcadeSimulation
{
    private double _bonusFeedbackSeconds;
    private LivingArcadeBonusFeedback? _recentBonus;
    private string _powerup = "", _negative = "";
    private double _powerupSeconds, _negativeSeconds;
    private int _negativeOrdinal;
    private int CharacterLevel => LivingArcadeHeroBalance.Progress(_hero.TotalKills).Level;
    private double TempoMultiplier => 1 + .04 * Math.Clamp(_level.Number - 1, 0, 10)
        + .1 * (1 - Math.Exp(-Math.Max(0, (double)_level.Number - 11) / 15));
    private double ProjectileDamage => (_hero.PermanentPower + _hero.WeaponPower - 1 + .025 * (CharacterLevel - 1))
        * WeaponDamageMultiplier * RewardDamageMultiplier
        * (_powerup == "power" ? 1.6 : 1) * (PenaltyActive && _penalty == "weak" ? .85 : 1);
    private double FireRate => Math.Min(20, _hero.FireRate * WeaponRateMultiplier * RewardFireRateMultiplier
        * (_powerup == "rapid" ? 1.6 : 1) * (_negative == "jam" ? .6 : 1));
    private int SpreadShots => Math.Min(5, WeaponShotCount + (_powerup == "spread" ? 2 : 0)
        + (RewardPattern == "fan" ? 2 : 0));

    private void TickPowerups(double seconds)
    {
        _powerupSeconds = Math.Max(0, _powerupSeconds - seconds);
        _negativeSeconds = Math.Max(0, _negativeSeconds - seconds);
        if (_powerupSeconds == 0) _powerup = "";
        if (_negativeSeconds == 0) _negative = "";
    }

    private void SpawnPickup(LivingArcadeMiniEvent mini)
    {
        var negative = mini.Kind == "negative";
        if (_pickups.Count >= 24 || negative && _pickups.Count(p => p.Negative) >= 2) return;
        var ordinal = negative ? _negativeOrdinal++ : _pickupOrdinal++;
        var kind = mini.BonusKind == "auto" ? LivingArcadePowerups.Choose(_level.Seed, _level.Number, ordinal, negative) : mini.BonusKind;
        _pickups.Add(new() { X = mini.X, Y = -.02, Strength = mini.Strength, Kind = kind, Negative = negative });
    }

    private void UpdatePickups(double worldSeconds)
    {
        foreach (var pickup in _pickups)
        {
            pickup.Age += worldSeconds;
            // Limited lateral pursuit, then fly past: no unavoidable instant contact or endless chase.
            if (pickup.Negative && pickup.Y < .89)
                pickup.X += Math.Clamp(_x - pickup.X, -.075 * worldSeconds, .075 * worldSeconds);
            pickup.Y += worldSeconds * (pickup.Negative ? .125 : .11);
        }
    }

    private void CollectBonus(Pickup pickup)
    {
        var damageBefore = ProjectileDamage;
        var healthBefore = _health;
        var rateBefore = FireRate;
        var shotsBefore = SpreadShots;
        var maximumBefore = _hero.MaxHealth;
        pickup.Collected = true;
        var duration = 0d;
        var companionAdded = false;
        if (pickup.Negative)
        {
            _negative = pickup.Kind;
            _negativeSeconds = _negativeSeconds > 0 ? Math.Min(_negativeSeconds, 6) : 6;
            duration = _negativeSeconds;
        }
        else
        {
            _bonusCount++;
            switch (pickup.Kind)
            {
                case "power": case "rapid": case "spread": case "shield": case "weapon_laser": case "weapon_rocket":
                    var maximum = pickup.Kind == "shield" ? 6 : 8;
                    // Replacement never refreshes the active window: repeated pickups cannot sustain invincibility.
                    _powerupSeconds = _powerupSeconds > 0 ? Math.Min(_powerupSeconds, maximum) : maximum;
                    _powerup = pickup.Kind; duration = _powerupSeconds; break;
                case "repair": _health = Math.Min(_hero.MaxHealth, _health + 12 * Math.Clamp(pickup.Strength, .5, 1.5)); break;
                case "weapon": _hero.WeaponPower = Math.Min(LivingArcadeHeroBalance.MaximumWeaponPower, _hero.WeaponPower + .02); break;
                case "cadence": _hero.FireRate = Math.Min(LivingArcadeHeroBalance.MaximumFireRate, _hero.FireRate + .08); break;
                case "vitality":
                    _hero.MaxHealth = Math.Min(LivingArcadeHeroBalance.MaximumHealth, _hero.MaxHealth + 2);
                    _health += _hero.MaxHealth - maximumBefore; break;
                case "time_scope": _selectiveTime = !_selectiveTime; _scopeBonusCount++; break;
                case "weapon_pistol": _hero.WeaponKind = "pistol"; break;
                case "weapon_shotgun": _hero.WeaponKind = "shotgun"; break;
                case "weapon_smg": _hero.WeaponKind = "smg"; break;
                case "helper": companionAdded = SummonHelper(); break;
            }
        }
        _recentBonus = new(ProjectileDamage - damageBefore, FireRate - rateBefore,
            SpreadShots - shotsBefore, _health - healthBefore, pickup.Kind == "time_scope")
        { Kind = pickup.Kind, DurationSeconds = duration, IsNegative = pickup.Negative,
            MaxHealthGain = _hero.MaxHealth - maximumBefore, CompanionAdded = companionAdded };
        _bonusFeedbackSeconds = 3;
        Sound?.Invoke(pickup.Negative ? "negative" : "bonus");
    }
}
