using System.IO;
using AIHub.Models;

namespace AIHub.Services.LivingArcade;

public sealed record LivingArcadeKillabilityProfile(double Damage, double FireRate, double TempoMultiplier);
public sealed record LivingArcadeKillabilityResult(double Budget, int HitCount, double? FirstHitSeconds,
    double DeadlineSeconds, bool NoWindow, string LimitingWeapon, bool ReachesContact, double? ContactSeconds);
public sealed record LivingArcadeSpawnPlan(double SpawnY, double EntranceHoldSeconds, bool ForceVulnerable,
    double Health, LivingArcadeKillabilityResult Evidence)
{
    public bool Adjusted { get; init; }
    public string AdjustmentReason { get; init; } = "";
}

/// <summary>A conditional, focused-fire guarantee, not a claim that every player can win a whole wave.</summary>
public static class LivingArcadeKillability
{
    private const double Lifetime = 35;
    public static LivingArcadeKillabilityProfile CreateProfile(LivingArcadeHeroState hero, int gameLevel)
    {
        var balanced = LivingArcadeHeroBalance.Normalize(hero);
        var rank = LivingArcadeHeroBalance.Progress(balanced.TotalKills).Level;
        var tempo = 1 + .04 * Math.Clamp(gameLevel - 1, 0, 10)
            + .1 * (1 - Math.Exp(-Math.Max(0, (double)gameLevel - 11) / 15));
        return new(balanced.PermanentPower + balanced.WeaponPower - 1 + .025 * (rank - 1), balanced.FireRate, tempo);
    }

    public static LivingArcadeKillabilityResult Evaluate(LivingArcadeEnemyDefinition definition, double spawnY,
        LivingArcadeKillabilityProfile profile, bool forceVulnerable = false, double entranceHoldSeconds = 0)
    {
        if (!double.IsFinite(spawnY) || !double.IsFinite(profile.Damage) || profile.Damage <= 0
            || !double.IsFinite(profile.FireRate) || profile.FireRate <= 0 || !double.IsFinite(profile.TempoMultiplier)
            || profile.TempoMultiplier < 1 || !double.IsFinite(entranceHoldSeconds) || entranceHoldSeconds is < 0 or > 8)
            throw new ArgumentOutOfRangeException(nameof(profile));
        LivingArcadeKillabilityResult? worst = null;
        var reachesContact = false;
        double? firstContact = null;
        var maximumTick = profile.TempoMultiplier * 1.5 / 60;
        // Even a strong temporary weapon can have a slower first shot. It may only lower this minimum.
        // No helpers, super, boss reward, power or rapid pickup. Count one central shotgun pellet.
        foreach (var weapon in new[] { "pistol", "shotgun", "smg", "laser", "rockets" })
        {
            var damage = profile.Damage * .85 * DamageMultiplier(weapon);
            var rate = Math.Min(20, profile.FireRate * RateMultiplier(weapon) * .6);
            var speed = ProjectileSpeed(weapon);
            var interval = 1 / Math.Clamp(rate, 1, 20);
            foreach (var scale in new[] { .5, 1, 1.5 })
            foreach (var selective in new[] { false, true })
            {
                var worldStep = profile.TempoMultiplier * scale / 60;
                var ownStep = profile.TempoMultiplier * (selective ? 1 : scale) / 60;
                var result = Forecast(definition, spawnY, speed, damage, interval, interval + ownStep,
                    worldStep, ownStep, forceVulnerable, entranceHoldSeconds, weapon);
                if (worst is null || result.Budget < worst.Budget) worst = result;
                if (result.ContactSeconds is { } contact)
                { reachesContact = true; firstContact = Math.Min(firstContact ?? contact, contact); }
            }
        }
        // A constant cadence can accidentally line up with a rotating shield. A separate opening-window
        // lower bound covers arbitrary fire phase and mid-fight time-mode switches as well.
        foreach (var weapon in new[] { "pistol", "shotgun", "smg", "laser", "rockets" })
        {
            var interval = 1 / Math.Clamp(Math.Min(20, profile.FireRate * RateMultiplier(weapon) * .6), 1, 20);
            var bound = SwitchingBound(definition, spawnY, profile, weapon, interval, maximumTick,
                forceVulnerable, entranceHoldSeconds, firstContact);
            if (bound.Budget < worst!.Budget) worst = bound;
            if (bound.ContactSeconds is { } contact)
            { reachesContact = true; firstContact = Math.Min(firstContact ?? contact, contact); }
        }
        return worst! with { ReachesContact = reachesContact, ContactSeconds = firstContact,
            DeadlineSeconds = firstContact ?? Lifetime };
    }

    public static LivingArcadeSpawnPlan CreateSpawnPlan(LivingArcadeEnemyDefinition definition, double requestedY,
        LivingArcadeKillabilityProfile profile, double difficulty = 1)
    {
        if (!double.IsFinite(difficulty) || difficulty <= 0) throw new ArgumentOutOfRangeException(nameof(difficulty));
        var y = Math.Min(requestedY, .70 - definition.Radius);
        var force = false;
        var hold = 0d;
        var evidence = Evaluate(definition, y, profile);
        var reason = y < requestedY ? "birth_above_weapon" : "";
        if (evidence.ReachesContact && evidence.NoWindow)
        {
            // A child may be born beside the player. Give it real travel time before touching HP.
            foreach (var candidateY in new[] { .5 - definition.Radius, .25 - definition.Radius, -.03 })
            {
                if (candidateY >= y) continue;
                y = candidateY; evidence = Evaluate(definition, y, profile); reason = "birth_moved_up_for_fire_window";
                if (!evidence.ReachesContact || !evidence.NoWindow) break;
            }
            if (evidence.ReachesContact && evidence.NoWindow)
            {
                force = true; evidence = Evaluate(definition, y, profile, true);
                reason = "shield_opened_for_fire_window";
            }
            if (evidence.ReachesContact && evidence.NoWindow)
            {
                y = Math.Max(y, -definition.Radius);
                foreach (var seconds in new[] { .5, 1, 2, 4, 8 })
                {
                    hold = seconds; evidence = Evaluate(definition, y, profile, true, hold);
                    reason = "telegraphed_entrance_fire_window";
                    if (!evidence.ReachesContact || !evidence.NoWindow) break;
                }
            }
        }
        if (evidence.ReachesContact && evidence.NoWindow) throw new InvalidDataException("LivingArcade.InvalidLevel");
        var raw = definition.Health * difficulty * LivingArcadeCombatRules.HealthMultiplier(definition);
        var health = evidence.ReachesContact ? Math.Min(raw, evidence.Budget) : raw;
        return new(y, hold, force, health, evidence)
        { Adjusted = reason.Length > 0 || health < raw, AdjustmentReason = reason.Length > 0 ? reason : health < raw ? "health_bounded_by_fire_window" : "" };
    }

    private static LivingArcadeKillabilityResult Forecast(LivingArcadeEnemyDefinition definition, double spawnY,
        double shotSpeed, double shotDamage, double interval, double fireClock, double worldStep, double ownStep,
        bool forceVulnerable, double hold, string weapon)
    {
        var states = definition.States.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var stateId = definition.InitialState;
        double y = spawnY, age = 0, stateTime = 0, elapsed = 0;
        var shots = new List<double>(24);
        var hits = new List<double>();
        var visible = false;
        var contact = false;
        while (age <= Lifetime)
        {
            var previousY = y;
            elapsed += worldStep;
            age += worldStep;
            if (age > Lifetime) break;
            var waiting = Math.Max(0, Math.Min(worldStep, hold - (elapsed - worldStep)));
            var movingSeconds = worldStep - waiting;
            var state = states[stateId];
            if (movingSeconds > 1e-10)
            {
                state = AdvanceState(states, ref stateId, ref stateTime, movingSeconds);
                if (state.Motion != "hold") y += definition.Speed * state.ForwardSpeed * movingSeconds;
            }
            visible |= y + definition.Radius >= 0;
            if (visible)
            {
                fireClock -= ownStep;
                if (fireClock <= 1e-10) { shots.Add(.82); fireClock += interval; }
            }
            for (var index = shots.Count - 1; index >= 0; index--)
            {
                var previousShot = shots[index];
                var shotY = previousShot - shotSpeed * ownStep;
                shots[index] = shotY;
                if (SweptHit(0, previousShot, 0, shotY, 0, previousY, 0, y, definition.Radius + .009, out var incoming))
                {
                    if (forceVulnerable || !BlocksHit(LivingArcadeLevelPolicy.ShieldMode(state),
                        NormalizeDegrees(state.WeakSpotAngleDegrees + state.ShieldRotationDegreesPerSecond * age),
                        state.WeakSpotWidthDegrees, incoming)) hits.Add(elapsed);
                    shots.RemoveAt(index);
                }
                else if (shotY < -.15) shots.RemoveAt(index);
            }
            if (y >= .86 - definition.Radius - .025) { contact = true; break; }
        }
        var deadline = Math.Min(elapsed, Lifetime);
        var safeHits = hits.Where(t => t <= deadline - 2 * worldStep + 1e-10).ToArray();
        return new(safeHits.Length * shotDamage * .8, safeHits.Length, safeHits.Length > 0 ? safeHits[0] : null,
            deadline, safeHits.Length == 0, weapon, contact, contact ? elapsed : null);
    }

    private static LivingArcadeKillabilityResult SwitchingBound(LivingArcadeEnemyDefinition definition, double spawnY,
        LivingArcadeKillabilityProfile profile, string weapon, double interval, double maximumTick,
        bool forceVulnerable, double hold, double? earlierContact)
    {
        var states = definition.States.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var stateId = definition.InitialState;
        double y = spawnY, age = 0, stateTime = 0;
        var frames = new List<OpeningFrame>(2200);
        var contact = false;
        while (age <= Lifetime)
        {
            var state = states[stateId];
            var mode = LivingArcadeLevelPolicy.ShieldMode(state);
            var weakAngle = NormalizeDegrees(state.WeakSpotAngleDegrees + state.ShieldRotationDegreesPerSecond * age);
            // The whole forward firing cone must fit, including the10degree tilt during lateral movement.
            var open = forceVulnerable || mode == "off" || mode == "rotating"
                && state.WeakSpotWidthDegrees >= 20
                && Math.Abs(NormalizeDegrees(180 - weakAngle)) <= state.WeakSpotWidthDegrees / 2 - 10;
            frames.Add(new(age, y, open && y + definition.Radius >= 0));
            if (y >= .86 - definition.Radius - .025) { contact = true; break; }
            var waiting = Math.Max(0, Math.Min(maximumTick, hold - age));
            age += maximumTick;
            var movingSeconds = maximumTick - waiting;
            if (movingSeconds > 1e-10)
            {
                state = AdvanceState(states, ref stateId, ref stateTime, movingSeconds);
                if (state.Motion != "hold") y += definition.Speed * state.ForwardSpeed * movingSeconds;
            }
        }
        var contactSeconds = contact ? Math.Min(age, Lifetime) : (double?)null;
        var deadline = Math.Min(earlierContact ?? Lifetime, contactSeconds ?? Lifetime);
        // Enemy forward movement is nonnegative. In own time it cannot stretch the gap between
        // successive arrivals beyond the firing gap. world/own never exceeds1.5 in any supported mode.
        var maximumArrivalGap = 1.5 * (interval + maximumTick);
        var minimumVerticalSpeed = ProjectileSpeed(weapon) * Math.Cos(Math.PI / 18) / 1.5;
        var hits = 0;
        double? firstHit = null;
        for (var index = 0; index < frames.Count; index++)
        {
            if (!frames[index].Open) continue;
            var start = index;
            while (index + 1 < frames.Count && frames[index + 1].Open) index++;
            var openStart = frames[start].At + 2 * maximumTick;
            var openEnd = Math.Min(frames[index].At, deadline) - 2 * maximumTick;
            var latestFirstLaunch = openStart + maximumArrivalGap;
            double? arrival = null;
            // Worst-case flight follows the actual approaching FSM rather than assuming a stationary
            // target. Only shots launched inside this same open window are relied on.
            for (var frameIndex = start; frameIndex <= index; frameIndex++)
            {
                var frame = frames[frameIndex];
                if (frame.At < latestFirstLaunch || frame.At > openEnd) continue;
                var projectileY = .82 - minimumVerticalSpeed * (frame.At - latestFirstLaunch);
                if (projectileY <= frame.Y + definition.Radius + .009)
                { arrival = frame.At; break; }
            }
            if (arrival is not { } at) continue;
            hits += 1 + (int)Math.Floor((openEnd - at) / maximumArrivalGap + 1e-10);
            firstHit ??= at;
        }
        var damage = profile.Damage * .85 * DamageMultiplier(weapon);
        return new(hits * damage * .8, hits, firstHit, deadline, hits == 0, weapon, contact, contactSeconds);
    }

    private readonly record struct OpeningFrame(double At, double Y, bool Open);
    private static double DamageMultiplier(string weapon) => weapon switch
    { "shotgun" => .6, "smg" => .5, "laser" => .8, "rockets" => 3, _ => 1 };
    private static double RateMultiplier(string weapon) => weapon switch
    { "shotgun" => .5, "smg" => 1.8, "laser" => 1.6, "rockets" => .4, _ => 1 };
    private static double ProjectileSpeed(string weapon) => weapon switch
    { "shotgun" => .72, "laser" => 1.5, "rockets" => .52, _ => .78 };

    internal static LivingArcadeEnemyState AdvanceState(IReadOnlyDictionary<string, LivingArcadeEnemyState> states,
        ref string stateId, ref double stateTime, double seconds)
    {
        stateTime += seconds;
        var state = states[stateId];
        if (stateTime + 1e-10 >= state.Duration)
        { stateId = state.NextState; stateTime -= state.Duration; state = states[stateId]; }
        return state;
    }

    internal static bool BlocksHit(string mode, double weakAngle, double weakWidth, double incomingAngle) =>
        mode == "full" || mode == "rotating" && Math.Abs(NormalizeDegrees(incomingAngle - weakAngle)) > weakWidth / 2;

    /// <summary>Relative swept circle: a small, fast target cannot fall between two rendered ticks.</summary>
    internal static bool SweptHit(double oldShotX, double oldShotY, double shotX, double shotY,
        double oldEnemyX, double oldEnemyY, double enemyX, double enemyY, double radius, out double incomingAngle)
    {
        var x = oldShotX - oldEnemyX; var y = oldShotY - oldEnemyY;
        var dx = shotX - enemyX - x; var dy = shotY - enemyY - y;
        var length = dx * dx + dy * dy;
        var nearest = length > 1e-20 ? Math.Clamp(-(x * dx + y * dy) / length, 0, 1) : 0;
        var nearX = x + nearest * dx; var nearY = y + nearest * dy;
        if (nearX * nearX + nearY * nearY > radius * radius) { incomingAngle = 0; return false; }
        var entry = 0d;
        var outside = x * x + y * y - radius * radius;
        if (outside > 0 && length > 1e-20)
        {
            var dot = x * dx + y * dy;
            entry = Math.Clamp((-dot - Math.Sqrt(Math.Max(0, dot * dot - length * outside))) / length, 0, 1);
        }
        var atX = x + entry * dx; var atY = y + entry * dy;
        if (Math.Abs(atX) + Math.Abs(atY) < 1e-10) { atX = -dx; atY = -dy; }
        incomingAngle = Math.Atan2(atX, -atY) * 180 / Math.PI;
        return true;
    }

    private static double NormalizeDegrees(double angle) => (angle % 360 + 540) % 360 - 180;
}
