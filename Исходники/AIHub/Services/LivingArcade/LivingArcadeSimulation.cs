using AIHub.Models;

namespace AIHub.Services.LivingArcade;

/// <summary>Fixed-step, budgeted game simulation independent of WPF, graphics and model execution.</summary>
public sealed partial class LivingArcadeSimulation
{
    private const double StepSeconds = 1d / 60;
    private readonly LivingArcadeLevel _level;
    private readonly LivingArcadeHeroState _hero;
    private readonly Dictionary<string, LivingArcadeEnemyDefinition> _types;
    private readonly Dictionary<string, IReadOnlyDictionary<string, LivingArcadeEnemyState>> _enemyStates;
    private readonly LivingArcadeKillabilityProfile _killabilityProfile;
    private readonly Dictionary<(string Type, double Y), LivingArcadeSpawnPlan> _spawnPlans = [];
    private readonly List<Spawn> _schedule;
    private readonly LivingArcadeMiniEvent[] _events;
    private readonly List<Enemy> _enemies = [];
    private readonly List<Shot> _shots = [];
    private readonly List<Pickup> _pickups = [];
    private readonly string _penalty;
    private readonly double _penaltyUntil;
    private readonly double _difficulty;
    private readonly int _attemptNumber;
    private bool _bossDefeated;
    private double _accumulator, _progress, _x = .5, _health, _damage, _angle, _fireClock;
    private double _upCooldown, _downCooldown, _superCooldown, _effectSeconds, _superFlash, _hurtGrace, _fog;
    private string _effect = "";
    private int _spawnIndex, _eventIndex, _serial, _bonusCount, _pickupOrdinal, _scopeBonusCount;
    private int _spawnBudget = 1200;
    private bool _complete, _defeated, _paused, _selectiveTime;
    private bool _penaltyCleared;
    private LivingArcadeInput _previous;
    private LivingArcadeInput _pending;

    public event Action<string>? Sound;
    public event Action? PenaltyCleared;
    public LivingArcadeHeroState ResultHero => LivingArcadeHeroBalance.Copy(_hero);
    public bool IsPaused => _paused;
    public IReadOnlyList<LivingArcadeEnemyDefinition> EnemyDefinitions => _visualEnemyDefinitions ?? _level.EnemyTypes;

    public LivingArcadeSimulation(LivingArcadeLevel level, LivingArcadeHeroState hero,
        int attempts = 0, double deathProgress = 0, string penalty = "")
    {
        LivingArcadeLevelPolicy.Validate(level);
        LivingArcadeCombatRules.ValidateHeroCombat(hero);
        _level = level; _hero = LivingArcadeHeroBalance.Normalize(hero); _health = _hero.MaxHealth;
        if (level.BossArt is not null) SetBossArt(level.BossArt);
        _attemptNumber = attempts;
        InitializeHelpers();
        _selectiveTime = level.SelectiveTime;
        _penalty = penalty; _penaltyUntil = deathProgress;
        _difficulty = Math.Max(.8, 1 - Math.Max(0, attempts - 3) * .05);
        if (level.Number % 3 == 0) _spawnBudget = 1199;
        _types = level.EnemyTypes.ToDictionary(x => x.Id, StringComparer.Ordinal);
        _enemyStates = level.EnemyTypes.ToDictionary(e => e.Id,
            e => (IReadOnlyDictionary<string, LivingArcadeEnemyState>)e.States.ToDictionary(s => s.Id, StringComparer.Ordinal), StringComparer.Ordinal);
        // Capture entry progression once. Positive pickups and level-ups never increase an existing enemy's HP.
        _killabilityProfile = LivingArcadeKillability.CreateProfile(_hero, level.Number);
        _events = level.MiniEvents.OrderBy(e => e.At).ToArray();
        _schedule = level.Waves.SelectMany(w => Enumerable.Range(0, w.Count).Select(i =>
            new Spawn(w.At + i * w.SpawnInterval, w.EnemyId,
                Math.Clamp(w.X + (i - (w.Count - 1) / 2d) * w.Spacing, .06, .94))))
            .OrderBy(x => x.At).ToList();
    }

    public void SetPaused(bool paused)
    {
        _paused = paused; _accumulator = 0; _previous = default; _pending = default;
    }

    public void Advance(double seconds, LivingArcadeInput input)
    {
        if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        if (_paused || _complete || _defeated) return;
        // An interrupted render never produces a burst of missed combat ticks.
        _accumulator += Math.Min(seconds, .25);
        _pending = new(input.Left, input.Right,
            _pending.SpeedUp || input.SpeedUp && !_previous.SpeedUp,
            _pending.SlowDown || input.SlowDown && !_previous.SlowDown,
            _pending.Super || input.Super && !_previous.Super);
        _previous = input;
        var ticks = 0;
        while (_accumulator + 1e-10 >= StepSeconds && ticks++ < 15 && !_complete && !_defeated)
        {
            Tick(StepSeconds, _pending);
            _pending = _pending with { SpeedUp = false, SlowDown = false, Super = false };
            _accumulator -= StepSeconds;
        }
    }

    private bool PenaltyActive => _penalty.Length > 0 && _progress < _penaltyUntil;
    private void Tick(double realSeconds, LivingArcadeInput input)
    {
        _upCooldown = Math.Max(0, _upCooldown - realSeconds);
        _downCooldown = Math.Max(0, _downCooldown - realSeconds);
        _superCooldown = Math.Max(0, _superCooldown - realSeconds);
        _effectSeconds = Math.Max(0, _effectSeconds - realSeconds);
        _superFlash = Math.Max(0, _superFlash - realSeconds);
        _hurtGrace = Math.Max(0, _hurtGrace - realSeconds);
        _fog = Math.Max(0, _fog - realSeconds);
        _bonusFeedbackSeconds = Math.Max(0, _bonusFeedbackSeconds - realSeconds);
        TickPowerups(realSeconds);
        if (_effectSeconds == 0) _effect = "";
        if (input.SpeedUp != input.SlowDown)
        {
            if (input.SpeedUp && _upCooldown == 0)
            { _effect = "speed_up"; _effectSeconds = 15; _upCooldown = 120; Sound?.Invoke("speed_up"); }
            if (input.SlowDown && _downCooldown == 0)
            { _effect = "slow_down"; _effectSeconds = 15; _downCooldown = 120; Sound?.Invoke("slow_down"); }
        }
        if (input.Super && _superCooldown == 0)
        {
            // Milestone ranks shorten a two-minute ability by only one second each (at most five).
            _superCooldown = 120 - Math.Min(5, CharacterLevel / 5); _superFlash = .4; Sound?.Invoke("super");
            foreach (var enemy in _enemies.Where(e => (e.IsBoss || e.Y > .25) && Math.Abs(e.X - _x) < .35).ToArray())
            {
                if (!enemy.IsBoss) Kill(enemy);
                else { enemy.Health -= 18; enemy.Flash = .15; if (enemy.Health <= 0) Kill(enemy); }
            }
            _shots.RemoveAll(s => s.Hostile && s.Y > .25);
        }
        var scale = _effect == "speed_up" ? 1.5 : _effect == "slow_down" ? .5 : 1;
        var worldSeconds = realSeconds * scale * TempoMultiplier;
        var ownSeconds = realSeconds * TempoMultiplier * (_selectiveTime ? 1 : scale);
        _progress += worldSeconds;
        if (!_penaltyCleared && _penalty.Length > 0 && _progress >= _penaltyUntil)
        { _penaltyCleared = true; PenaltyCleared?.Invoke(); }
        var before = _x;
        _x = Math.Clamp(_x + (input.Right.CompareTo(input.Left)) * .46 * ownSeconds
            * (PenaltyActive && _penalty == "heavy" ? .85 : 1) * (_negative == "drag" ? .65 : 1), .065, .935);
        _angle = Math.Sign(_x - before) * Math.PI / 18;
        _fireClock -= ownSeconds;
        if (_fireClock <= 0)
        {
            _fireClock += 1 / Math.Clamp(FireRate, 1, 20);
            ShootPlayerVolley();
            Sound?.Invoke("shoot");
        }
        while (_spawnIndex < _schedule.Count && _schedule[_spawnIndex].At <= _progress)
        { var spawn = _schedule[_spawnIndex++]; SpawnEnemy(spawn.Type, spawn.X, -.03); }
        while (_eventIndex < _events.Length && _events[_eventIndex].At <= _progress) ApplyEvent(_events[_eventIndex++]);
        foreach (var enemy in _enemies.ToArray()) UpdateEnemy(enemy, worldSeconds);
        UpdateHelpers(ownSeconds);
        foreach (var shot in _shots)
        {
            shot.PreviousX = shot.X; shot.PreviousY = shot.Y;
            var seconds = shot.Hostile ? worldSeconds : ownSeconds;
            shot.Age += seconds;
            if (shot.ReturnAfter > 0 && shot.Age >= shot.ReturnAfter)
            {
                shot.Returning = true;
                var owner = shot.Owner is { Removed: false } ? shot.Owner : null;
                var targetX = owner?.X ?? shot.OriginX;
                var targetY = owner?.Y ?? shot.OriginY;
                var distance = Distance(shot.X, shot.Y, targetX, targetY);
                if (distance <= Math.Max(owner?.Definition.Radius ?? .012, shot.Speed * seconds))
                { shot.Spent = true; continue; }
                shot.Vx = (targetX - shot.X) / distance * shot.Speed;
                shot.Vy = (targetY - shot.Y) / distance * shot.Speed;
            }
            shot.X += shot.Vx * seconds; shot.Y += shot.Vy * seconds;
            if (shot.Wave && !shot.Returning) shot.X = shot.OriginX + shot.Vx * shot.Age + Math.Sin(shot.Age * 12) * .035;
        }
        _shots.RemoveAll(s => s.X < -.1 || s.X > 1.1 || s.Y < -.15 || s.Y > 1.15 || s.Spent
            || s.ReturnAfter > 0 && s.Age >= 12);
        UpdatePickups(worldSeconds);
        Collide();
        _pickups.RemoveAll(p => p.Y > 1.1 || p.Age >= 14 || p.Collected);
        if (!_defeated && _progress >= _level.DurationSeconds && _spawnIndex >= _schedule.Count
            && _eventIndex >= _events.Length && _enemies.Count == 0 && !TryStartBoss())
        { _complete = true; Sound?.Invoke("complete"); }
    }

    private void UpdateEnemy(Enemy enemy, double seconds)
    {
        if (!_enemies.Contains(enemy)) return;
        enemy.PreviousX = enemy.X; enemy.PreviousY = enemy.Y;
        if (enemy.IsBoss) { UpdateBoss(enemy, seconds); return; }
        // An entrance warning spends the existing lifetime; it never extends population residency.
        enemy.Age += seconds;
        if (enemy.Age > 35) { enemy.Removed = true; _enemies.Remove(enemy); return; }
        if (enemy.EntranceHoldRemaining > 0)
        {
            var waiting = Math.Min(seconds, enemy.EntranceHoldRemaining);
            enemy.EntranceHoldRemaining -= waiting; seconds -= waiting;
            enemy.Telegraph = true; enemy.ShieldMode = "off";
            if (seconds <= 1e-10) return;
        }
        enemy.Flash = Math.Max(0, enemy.Flash - seconds);
        var state = LivingArcadeKillability.AdvanceState(_enemyStates[enemy.Definition.Id], ref enemy.State, ref enemy.StateTime, seconds);
        var drift = state.Motion switch
        {
            "hold" => 0,
            "sine" => Math.Cos(enemy.Age * 2.5) * state.HorizontalSpeed,
            "zigzag" => Math.Sign(Math.Sin(enemy.Age * 2)) * state.HorizontalSpeed,
            "orbit" => Math.Cos(enemy.Age * 4) * Math.Max(.1, Math.Abs(state.HorizontalSpeed)),
            "hunt" => Math.Sign(_x - enemy.X) * Math.Abs(state.HorizontalSpeed),
            _ => state.HorizontalSpeed
        };
        var edge = Math.Max(.045, enemy.Definition.Radius);
        enemy.X = Math.Clamp(enemy.X + drift * seconds, edge, 1 - edge);
        if (state.Motion != "hold") enemy.Y += enemy.Definition.Speed * state.ForwardSpeed * seconds;
        enemy.ShieldMode = enemy.ForceVulnerable ? "off" : LivingArcadeLevelPolicy.ShieldMode(state);
        enemy.WeakSpotAngle = NormalizeDegrees(state.WeakSpotAngleDegrees + state.ShieldRotationDegreesPerSecond * enemy.Age);
        enemy.WeakSpotWidth = state.WeakSpotWidthDegrees;
        enemy.Telegraph = state.Telegraph;
        enemy.FireClock -= seconds;
        if (state.ShotInterval > 0 && enemy.FireClock <= 0 && enemy.Y > 0 && enemy.Y < .74)
        {
            enemy.FireClock = state.ShotInterval;
            var aim = Math.Atan2(_x - enemy.X, .86 - enemy.Y);
            for (var i = 0; i < state.ShotCount; i++)
            {
                var spread = state.ShotCount == 1 ? 0 : ((double)i / (state.ShotCount - 1) - .5) * state.ShotSpread * Math.PI / 180;
                AddShot(enemy.X, enemy.Y, aim + spread, state.ShotSpeed, true, 9 * _difficulty,
                    state.ShotReturnAfterSeconds, enemy);
            }
        }
        if (enemy.Y > 1.1 || enemy.Age > 35) { enemy.Removed = true; _enemies.Remove(enemy); }
    }

    private void ApplyEvent(LivingArcadeMiniEvent mini)
    {
        switch (mini.Kind)
        {
            case "bonus":
            case "negative":
                SpawnPickup(mini);
                break;
            case "ambush": SpawnEnemy(mini.EnemyId!, mini.X, -.02); break;
            case "meteor": AddShot(mini.X, 0, .05 * Math.Sin(mini.At), .23, true, 12 * mini.Strength); break;
            case "fog": _fog = 3 * mini.Strength; break;
        }
    }

    private void Collide()
    {
        foreach (var shot in _shots.Where(s => !s.Spent).ToArray())
        {
            if (shot.Hostile)
            {
                if (TryHitHelper(shot)) continue;
                if (Distance(shot.X, shot.Y, _x, .86) < .025 + .009)
                { shot.Spent = true; Hurt(shot.Damage); }
                continue;
            }
            foreach (var enemy in _enemies.ToArray())
            {
                if (shot.HitEnemies.Contains(enemy)) continue;
                if (!LivingArcadeKillability.SweptHit(shot.PreviousX, shot.PreviousY, shot.X, shot.Y,
                    enemy.PreviousX, enemy.PreviousY, enemy.X, enemy.Y, enemy.Definition.Radius + .009, out var incomingAngle)) continue;
                var blocked = LivingArcadeKillability.BlocksHit(enemy.ShieldMode, enemy.WeakSpotAngle, enemy.WeakSpotWidth, incomingAngle);
                ApplyPlayerShotHit(shot, enemy, blocked);
                if (shot.Spent) break;
            }
        }
        foreach (var enemy in _enemies.ToArray())
        {
            if (CollideHelpersWithEnemy(enemy)) continue;
            if (Distance(enemy.X, enemy.Y, _x, .86) < enemy.Definition.Radius + .025)
            { enemy.Removed = true; _enemies.Remove(enemy); Hurt(14 * _difficulty); }
        }
        foreach (var pickup in _pickups.Where(p => !p.Collected))
            if (Distance(pickup.X, pickup.Y, _x, .86) < .05)
            {
                CollectBonus(pickup);
            }
    }

    private void Hurt(double amount)
    {
        if (_hurtGrace > 0 || _defeated || _powerup == "shield") return;
        amount *= PenaltyActive && _penalty == "fragile" ? 1.15 : 1;
        amount *= _negative == "fragile" ? 1.3 : 1;
        _health = Math.Max(0, _health - amount); _damage += amount; _hurtGrace = .25;
        Sound?.Invoke("hit");
        if (_health == 0) { _defeated = true; Sound?.Invoke("defeat"); }
    }
    private void Kill(Enemy enemy)
    {
        if (!_enemies.Remove(enemy)) return;
        enemy.Removed = true;
        var previousLevel = CharacterLevel;
        var experience = enemy.ExperienceValue;
        _hero.TotalKills = _hero.TotalKills > long.MaxValue - experience ? long.MaxValue : _hero.TotalKills + experience;
        if (enemy.IsBoss) _bossDefeated = true;
        if (CharacterLevel > previousLevel) { _superFlash = .4; Sound?.Invoke("bonus"); }
        Sound?.Invoke("enemy_down");
        for (var i = 0; i < enemy.Definition.DeathSpawnCount; i++)
            SpawnEnemy(enemy.Definition.DeathSpawnId!, Math.Clamp(enemy.X + (i - (enemy.Definition.DeathSpawnCount - 1) / 2d) * .05, .05, .95), enemy.Y);
    }
    private void SpawnEnemy(string type, double x, double y)
    {
        if (_spawnBudget-- <= 0 || _enemies.Count >= 80 || !_types.TryGetValue(type, out var definition)) return;
        var experience = LivingArcadeCombatRules.ExperienceValue(definition);
        if (!_spawnPlans.TryGetValue((type, y), out var plan))
        {
            plan = LivingArcadeKillability.CreateSpawnPlan(definition, y, _killabilityProfile, _difficulty);
            _spawnPlans.Add((type, y), plan);
        }
        _enemies.Add(new() { Id = type + "-" + ++_serial, Definition = definition, X = x, Y = plan.SpawnY,
            PreviousX = x, PreviousY = plan.SpawnY, State = definition.InitialState,
            Health = plan.Health, MaximumHealth = plan.Health, FireClock = .8,
            ExperienceValue = experience, ForceVulnerable = plan.ForceVulnerable,
            EntranceHoldRemaining = plan.EntranceHoldSeconds, Telegraph = plan.EntranceHoldSeconds > 0 });
    }
    private void AddShot(double x, double y, double angle, double speed, bool hostile, double damage,
        double returnAfter = 0, Enemy? owner = null)
    {
        if (_shots.Count >= 512) return;
        _shots.Add(new() { X = x, Y = y, Vx = Math.Sin(angle) * speed, Vy = (hostile ? 1 : -1) * Math.Cos(angle) * speed,
            Hostile = hostile, Damage = damage, Speed = speed, OriginX = x, OriginY = y,
            ReturnAfter = returnAfter, Owner = owner });
    }

    public LivingArcadeSnapshot Snapshot => new()
    {
        PlayerX = _x, Health = _health, MaxHealth = _hero.MaxHealth, Progress = _progress,
        DurationSeconds = _level.DurationSeconds, AngleRadians = _angle, SpeedUpCooldown = _upCooldown,
        SlowDownCooldown = _downCooldown, SuperCooldown = _superCooldown, EffectSeconds = _effectSeconds,
        ActiveEffect = _fog > 0 ? "fog" : _effect, SuperFlash = _superFlash, IsComplete = _complete,
        TimeEffect = _effect, SelectiveTime = _selectiveTime, TimeScopeBonusCount = _scopeBonusCount,
        IsDefeated = _defeated, IsPaused = _paused, DamageTaken = _damage, BonusCount = _bonusCount,
        ProjectileDamage = ProjectileDamage, FireRate = FireRate, SpreadShots = SpreadShots,
        TempoMultiplier = TempoMultiplier, PowerupEffect = _powerup, PowerupSeconds = _powerupSeconds,
        NegativeEffect = _negative, NegativeSeconds = _negativeSeconds,
        GameLevel = _level.Number, CharacterLevel = CharacterLevel,
        KillsTowardNextLevel = LivingArcadeHeroBalance.Progress(_hero.TotalKills).KillsTowardNextLevel,
        KillsRequiredForNextLevel = LivingArcadeHeroBalance.Progress(_hero.TotalKills).KillsRequiredForNextLevel,
        BossPhase = ActiveBossPhase, BossHealth = ActiveBossHealth, BossMaxHealth = ActiveBossMaximumHealth,
        BossDefeated = _bossDefeated, WeaponKind = CurrentWeapon, WeaponSeconds = CurrentWeaponSeconds,
        BossReward = ActiveReward is { } reward ? reward with { Reward = LivingArcadeRewardPolicy.Copy(reward.Reward) } : null,
        Companions = CompanionViews,
        BonusFeedbackSeconds = _bonusFeedbackSeconds, RecentBonus = _bonusFeedbackSeconds > 0 ? _recentBonus : null,
        Penalty = PenaltyActive ? _penalty : "", PenaltyUntil = _penaltyUntil,
        Enemies = _enemies.Select(e => new LivingArcadeEntityView(e.Id, e.X, e.Y, e.Definition.Radius,
            e.ShieldMode != "off" ? "#73DACA" : e.Definition.Color, e.Definition.Shape,
            Math.Clamp(e.Health / (e.MaximumHealth > 0 ? e.MaximumHealth : e.Definition.Health * _difficulty), 0, 1), true, e.Flash,
            e.Definition.Visual, e.Age, e.Telegraph, e.ShieldMode, e.WeakSpotAngle, e.WeakSpotWidth,
            IsBoss: e.IsBoss, BossPhase: e.BossPhase, ExperienceValue: e.ExperienceValue)
            { Health = e.Health, MaxHealth = e.MaximumHealth > 0 ? e.MaximumHealth : e.Definition.Health * _difficulty }).ToArray(),
        Shots = _shots.Where(s => !s.Spent).Select(s => new LivingArcadeEntityView(s.Kind, s.X, s.Y, .007,
            s.Color.Length > 0 ? s.Color : s.Hostile ? "#FF757F" : "#FDF09E", "circle", Hostile: s.Hostile, Age: s.Age, Returning: s.Returning)
            { HeadingRadians = Math.Atan2(s.Vx, -s.Vy) }).ToArray(),
        Pickups = _pickups.Select(p => new LivingArcadeEntityView((p.Negative ? "negative_" : "bonus_") + p.Kind,
            p.X, p.Y, .025, p.Negative ? "#FF757F" : p.Kind == "time_scope" ? "#66CCFF" : "#72F1B8",
            p.Negative ? "diamond" : "star", Hostile: p.Negative, Age: p.Age)).ToArray()
    };

    private static double Distance(double x, double y, double x2, double y2) => Math.Sqrt((x - x2) * (x - x2) + (y - y2) * (y - y2));
    private static double NormalizeDegrees(double angle) => (angle % 360 + 540) % 360 - 180;
    private sealed record Spawn(double At, string Type, double X);
    private sealed class Enemy
    {
        public string Id = ""; public LivingArcadeEnemyDefinition Definition = new();
        public string State = "", ShieldMode = "off";
        public double X, Y, PreviousX, PreviousY, Health, MaximumHealth, Age, StateTime, FireClock, Flash, WeakSpotAngle, WeakSpotWidth, EntranceHoldRemaining;
        public bool Telegraph, Removed, IsBoss, ForceVulnerable;
        public int BossPhase, ExperienceValue = 1;
    }
    private sealed class Shot
    {
        public double X, Y, PreviousX, PreviousY, Vx, Vy, Damage, Age, Speed, OriginX, OriginY, ReturnAfter;
        public bool Hostile, Spent, Returning, Wave; public Enemy? Owner;
        public int Pierces;
        public double SplashRadius;
        public string Kind = "shot", Color = "";
        public HashSet<Enemy> HitEnemies = [];
    }
    private sealed class Pickup
    {
        public double X, Y, Strength, Age;
        public bool Collected, Negative;
        public string Kind = "repair";
    }
}
