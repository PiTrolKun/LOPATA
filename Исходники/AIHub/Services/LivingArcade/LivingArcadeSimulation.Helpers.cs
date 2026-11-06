using AIHub.Models;

namespace AIHub.Services.LivingArcade;

public sealed partial class LivingArcadeSimulation
{
    private readonly List<Helper> _helpers = [];

    private void InitializeHelpers()
    {
        _hero.Companions = _hero.Companions.Where(c => c.Health > 0).Select(c => c with { }).ToList();
        foreach (var state in _hero.Companions)
            _helpers.Add(new() { State = state, X = _x, Y = .79, FireClock = .35 + _helpers.Count * .3 });
    }

    private bool SummonHelper(string kind = "drone")
    {
        if (_helpers.Count >= 2) return false;
        if (kind is not ("drone" or "gunner")) throw new ArgumentOutOfRangeException(nameof(kind));
        string id;
        do { id = $"helper-{_level.Number}-{_attemptNumber}-{++_serial}"; }
        while (_hero.Companions.Any(c => c.Id == id));
        var state = new LivingArcadeCompanion { Id = id, Kind = kind, Health = 24, MaxHealth = 24 };
        _hero.Companions.Add(state);
        _helpers.Add(new() { State = state, X = _x, Y = .79, FireClock = .25 });
        Sound?.Invoke("bonus");
        return true;
    }

    private void UpdateHelpers(double seconds)
    {
        for (var index = 0; index < _helpers.Count; index++)
        {
            var helper = _helpers[index];
            helper.Age += seconds;
            helper.HurtGrace = Math.Max(0, helper.HurtGrace - seconds);
            var targetX = Math.Clamp(_x + (index % 2 == 0 ? -.085 : .085), .035, .965);
            helper.X += Math.Clamp(targetX - helper.X, -.35 * seconds, .35 * seconds);
            helper.FireClock -= seconds;
            if (helper.FireClock > 0) continue;
            helper.FireClock += 1 / 1.5;
            if (_shots.Count >= 512) continue;
            _shots.Add(new()
            {
                X = helper.X, Y = helper.Y - .025, Vx = 0, Vy = -.76, Speed = .76,
                Damage = .7, Kind = "helper_shot", OriginX = helper.X, OriginY = helper.Y - .025
            });
        }
    }

    private bool TryHitHelper(Shot shot)
    {
        if (!shot.Hostile) return false;
        foreach (var helper in _helpers)
        {
            if (Distance(shot.X, shot.Y, helper.X, helper.Y) >= .031) continue;
            shot.Spent = true;
            HurtHelper(helper, shot.Damage);
            return true;
        }
        return false;
    }

    private bool CollideHelpersWithEnemy(Enemy enemy)
    {
        foreach (var helper in _helpers)
        {
            if (Distance(enemy.X, enemy.Y, helper.X, helper.Y) >= enemy.Definition.Radius + .022) continue;
            if (!enemy.IsBoss) { enemy.Removed = true; _enemies.Remove(enemy); }
            HurtHelper(helper, 14 * _difficulty);
            return !enemy.IsBoss;
        }
        return false;
    }

    private void HurtHelper(Helper helper, double damage)
    {
        if (helper.HurtGrace > 0) return;
        helper.State.Health = Math.Max(0, helper.State.Health - damage);
        helper.HurtGrace = .25;
        Sound?.Invoke("hit");
        if (helper.State.Health > 0) return;
        // No resurrection on the next stage or on a repair pickup: only a new rare pickup can summon again.
        _helpers.Remove(helper);
        _hero.Companions.Remove(helper.State);
        Sound?.Invoke("enemy_down");
    }

    private IReadOnlyList<LivingArcadeEntityView> CompanionViews => _helpers.Select(helper =>
        new LivingArcadeEntityView("helper_" + helper.State.Kind + "_" + helper.State.Id,
            helper.X, helper.Y, .022, helper.State.Kind == "gunner" ? "#F0C674" : "#6FE7E0", "diamond",
            Math.Clamp(helper.State.Health / helper.State.MaxHealth, 0, 1), Hostile: false,
            Flash: helper.HurtGrace, Age: helper.Age)
        { Health = helper.State.Health, MaxHealth = helper.State.MaxHealth }).ToArray();

    private sealed class Helper
    {
        public LivingArcadeCompanion State = new();
        public double X, Y, Age, FireClock, HurtGrace;
    }
}
