using System.Text.Json.Serialization;
using System.Reflection;

namespace AIHub.Models;

public sealed record LivingArcadeHeroState
{
    public int PowerupBalanceVersion { get; set; }
    public long TotalKills { get; set; }
    public double PermanentPower { get; set; } = 1;
    public double WeaponPower { get; set; } = 1;
    public double MaxHealth { get; set; } = 100;
    public double FireRate { get; set; } = 5;
    public int SpreadShots { get; set; } = 1;
    public string WeaponKind { get; set; } = "pistol";
    public List<LivingArcadeCompanion> Companions { get; set; } = [];
    public LivingArcadeActiveReward? ActiveBossReward { get; set; }
}

public sealed record LivingArcadeEnemyState
{
    [JsonRequired]
    public string Id { get; set; } = "advance";
    [JsonRequired]
    public double Duration { get; set; } = 4;
    [JsonRequired]
    public string NextState { get; set; } = "advance";
    public string Motion { get; set; } = "line";
    public double ForwardSpeed { get; set; } = 1;
    public double HorizontalSpeed { get; set; }
    public double ShotInterval { get; set; }
    public int ShotCount { get; set; } = 1;
    public double ShotSpread { get; set; }
    public double ShotSpeed { get; set; } = .25;
    public bool Shield { get; set; }
    // Omitted fields retain the original v1 save behaviour.
    public string ShieldMode { get; set; } = "legacy";
    public double WeakSpotAngleDegrees { get; set; } = 180;
    public double WeakSpotWidthDegrees { get; set; } = 90;
    public double ShieldRotationDegreesPerSecond { get; set; } = 90;
    public bool Telegraph { get; set; }
    public double ShotReturnAfterSeconds { get; set; }
}

public sealed record LivingArcadeEnemyDefinition
{
    [JsonRequired]
    public string Id { get; set; } = "scout";
    public string TitleRu { get; set; } = "Разведчик";
    public string TitleEn { get; set; } = "Scout";
    public double Health { get; set; } = 3;
    public double Speed { get; set; } = .07;
    public double Radius { get; set; } = .025;
    public string Shape { get; set; } = "diamond";
    public string Color { get; set; } = "#F07178";
    public string EncounterClass { get; set; } = "normal";
    public LivingArcadeVisual? Visual { get; set; }
    [JsonRequired]
    public string InitialState { get; set; } = "advance";
    [JsonRequired]
    public List<LivingArcadeEnemyState> States { get; set; } = [new()];
    public string? DeathSpawnId { get; set; }
    public int DeathSpawnCount { get; set; }
}

public sealed record LivingArcadeWave
{
    public double At { get; set; }
    public string EnemyId { get; set; } = "scout";
    public int Count { get; set; } = 3;
    public double X { get; set; } = .5;
    public double Spacing { get; set; } = .11;
    public double SpawnInterval { get; set; } = .3;
}

public sealed record LivingArcadeMiniEvent
{
    public double At { get; set; }
    public string Kind { get; set; } = "bonus";
    public double X { get; set; } = .5;
    public double Strength { get; set; } = 1;
    public string? EnemyId { get; set; }
    public bool Tutorial { get; set; }
    public string BonusKind { get; set; } = "auto";
}

public sealed record LivingArcadeModelCredit(string Role, string ModelId, string Revision);
public sealed record LivingArcadeAssetCredit(string AssetId,string LibraryRevision,string PackId,string Author,
    string License,string SourceUrl,List<LivingArcadeModelCredit> Models);

public sealed record LivingArcadeLevel
{
    public List<LivingArcadeAssetCredit> AssetCredits { get; set; } = [];
    [JsonRequired]
    public int SchemaVersion { get; set; } = 1;
    [JsonRequired]
    public int Number { get; set; } = 1;
    [JsonRequired]
    public string TitleRu { get; set; } = "Первый путь";
    [JsonRequired]
    public string TitleEn { get; set; } = "The first road";
    public string DescriptionRu { get; set; } = "";
    public string DescriptionEn { get; set; } = "";
    [JsonRequired]
    public double DurationSeconds { get; set; } = 120;
    public int Seed { get; set; }
    public bool Authored { get; set; }
    [JsonRequired]
    public bool ResetDifficulty { get; set; }
    [JsonRequired]
    public double CarryFraction { get; set; } = 1;
    public bool SelectiveTime { get; set; }
    public string BackgroundColor { get; set; } = "#101A2C";
    public LivingArcadeGeneratedScenery? Scenery { get; set; }
    public LivingArcadeBossArt? BossArt { get; set; }
    [JsonRequired]
    public List<LivingArcadeEnemyDefinition> EnemyTypes { get; set; } = [];
    [JsonRequired]
    public List<LivingArcadeWave> Waves { get; set; } = [];
    public List<LivingArcadeMiniEvent> MiniEvents { get; set; } = [];
    public List<int> MusicNotes { get; set; } = [60, 64, 67, 72, 67, 64, 62, 67];
    public List<string> Mechanics { get; set; } = [];
    public List<LivingArcadeModelCredit> Credits { get; set; } = [];
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public string Producer { get; set; } = "LOPATA";
    public string ProducerVersion { get; set; } = typeof(LivingArcadeLevel).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
}

public sealed record LivingArcadeDraft
{
    public int Number { get; set; }
    public string Concept { get; set; } = "";
    public string NewMechanic { get; set; } = "";
}

public sealed record LivingArcadeLevelResult
{
    public int Level { get; set; }
    public double Seconds { get; set; }
    public double DamageTaken { get; set; }
    public int BonusCount { get; set; }
    public int Attempts { get; set; }
    public List<string> Mechanics { get; set; } = [];
    public DateTimeOffset CompletedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record LivingArcadeAttempt
{
    public int Version { get; set; } = 1;
    public string Algorithm { get; set; } = "xorshift32-v1";
    public string SimulationVersion { get; set; } = "fixed60-v4";
    public uint Seed { get; set; }
    public int Level { get; set; }
    public int Number { get; set; }
    public List<LivingArcadeMiniEvent> MiniEvents { get; set; } = [];
}

public sealed record LivingArcadeRun
{
    [JsonRequired]
    public int SchemaVersion { get; set; } = 1;
    public string Producer { get; set; } = "LOPATA";
    public string ProducerVersion { get; set; } = typeof(LivingArcadeRun).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
    [JsonRequired]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public int Seed { get; set; }
    public int CurrentLevel { get; set; } = 1;
    public int Attempts { get; set; }
    public double DeathProgress { get; set; }
    public string Penalty { get; set; } = "";
    public LivingArcadeHeroState EntryState { get; set; } = new();
    public LivingArcadeHeroState? PendingHero { get; set; }
    public Dictionary<int, LivingArcadeBossReward> BossRewards { get; set; } = [];
    public int? PendingBossRewardLevel { get; set; }
    public bool FirstEventsPrepared { get; set; }
    public LivingArcadeAttempt? Attempt { get; set; }
    public Dictionary<int, LivingArcadeLevel> Levels { get; set; } = [];
    public LivingArcadeDraft? Draft { get; set; }
    public List<LivingArcadeLevelResult> History { get; set; } = [];
    public int Revision { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    [JsonIgnore]
    public bool RecoveredFromBackup { get; set; }
}

public readonly record struct LivingArcadeInput(bool Left = false, bool Right = false,
    bool SpeedUp = false, bool SlowDown = false, bool Super = false);

public sealed record LivingArcadeEntityView(string Id, double X, double Y, double Radius,
    string Color, string Shape, double HealthFraction = 1, bool Hostile = true, double Flash = 0,
    LivingArcadeVisual? Visual = null, double Age = 0, bool Telegraph = false,
    string ShieldMode = "off", double WeakSpotAngleDegrees = 180, double WeakSpotWidthDegrees = 90,
    bool Returning = false, bool IsBoss = false, int BossPhase = 0, int ExperienceValue = 1)
{
    public double Health { get; init; }
    public double MaxHealth { get; init; }
    /// <summary>Render-only direction of a projectile, measured from world up. Not stored in run state.</summary>
    public double? HeadingRadians { get; init; }
}

public sealed record LivingArcadeBonusFeedback(double DamageGain, double FireRateGain, int ExtraShots,
    double HealthRestored, bool TimeScopeChanged)
{
    public string Kind { get; init; } = "";
    public double DurationSeconds { get; init; }
    public double MaxHealthGain { get; init; }
    public bool IsNegative { get; init; }
    public bool CompanionAdded { get; init; }
}

public sealed record LivingArcadeSnapshot
{
    public double PlayerX { get; init; } = .5;
    public double PlayerY { get; init; } = .86;
    public double Health { get; init; } = 100;
    public double MaxHealth { get; init; } = 100;
    public double Progress { get; init; }
    public double DurationSeconds { get; init; } = 120;
    public double AngleRadians { get; init; }
    public double SpeedUpCooldown { get; init; }
    public double SlowDownCooldown { get; init; }
    public double SuperCooldown { get; init; }
    public double EffectSeconds { get; init; }
    public string ActiveEffect { get; init; } = "";
    public string TimeEffect { get; init; } = "";
    public bool SelectiveTime { get; init; }
    public int TimeScopeBonusCount { get; init; }
    public double SuperFlash { get; init; }
    public bool IsComplete { get; init; }
    public bool IsDefeated { get; init; }
    public bool IsPaused { get; init; }
    public double DamageTaken { get; init; }
    public int BonusCount { get; init; }
    public double ProjectileDamage { get; init; } = 1;
    public double FireRate { get; init; } = 5;
    public int SpreadShots { get; init; } = 1;
    public double BonusFeedbackSeconds { get; init; }
    public double TempoMultiplier { get; init; } = 1;
    public string PowerupEffect { get; init; } = "";
    public double PowerupSeconds { get; init; }
    public string NegativeEffect { get; init; } = "";
    public double NegativeSeconds { get; init; }
    public int GameLevel { get; init; } = 1;
    public int CharacterLevel { get; init; } = 1;
    public long KillsTowardNextLevel { get; init; }
    public long KillsRequiredForNextLevel { get; init; } = 10;
    public int BossPhase { get; init; }
    public double BossHealth { get; init; }
    public double BossMaxHealth { get; init; }
    public bool BossDefeated { get; init; }
    public string WeaponKind { get; init; } = "pistol";
    public double WeaponSeconds { get; init; }
    public LivingArcadeActiveReward? BossReward { get; init; }
    public IReadOnlyList<LivingArcadeEntityView> Companions { get; init; } = [];
    public LivingArcadeBonusFeedback? RecentBonus { get; init; }
    public string Penalty { get; init; } = "";
    public double PenaltyUntil { get; init; }
    public IReadOnlyList<LivingArcadeEntityView> Enemies { get; init; } = [];
    public IReadOnlyList<LivingArcadeEntityView> Shots { get; init; } = [];
    public IReadOnlyList<LivingArcadeEntityView> Pickups { get; init; } = [];
}

public sealed record LivingArcadeProbeTrajectory(string Name, double Progress, double DamageTaken,
    bool Completed, bool Defeated, bool ReachedTimeBound, int PeakEnemies, int PeakShots);

/// <summary>Cost and smoke evidence, never a proof that every human can win the stage.</summary>
public sealed record LivingArcadeProbeResult(bool Accepted, string? RejectionKey, int ExpandedSpawnCount,
    int PeakScheduledEnemies, double EstimatedPeakHostileShots, bool RuntimeTruncated,
    int SimulationTicks, double ElapsedMilliseconds, IReadOnlyList<LivingArcadeProbeTrajectory> Trajectories);
