using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Models;

namespace AIHub.Controls.LivingArcade;

/// <summary>Three native HUD surfaces; the page chooses their placement around the playfield.</summary>
public sealed partial class LivingArcadeGameHud
{
    private readonly Func<string, string> _l;
    private readonly Func<string> _language;
    private readonly TextBlock _characterLevel, _damage, _fireRate, _shots, _tempo, _weapon;
    private readonly TextBlock _companions, _reward, _powerup, _negative, _penalty, _pickup;
    private readonly TextBlock _gameLevel, _stageTime, _boss, _timeScope, _timeEffect;
    private readonly Meter _health, _experience, _stageProgress, _bossHealth;
    private readonly Ability _speedUp, _slowDown, _super;
    private readonly TextBlock _heroTitle, _healthTitle, _damageTitle, _fireRateTitle, _shotsTitle;

    public FrameworkElement HeroPanel { get; }
    public FrameworkElement StageHeader { get; }
    public FrameworkElement Abilities { get; }

    public LivingArcadeGameHud(Func<string, string> localize, Func<string> language)
    {
        ArgumentNullException.ThrowIfNull(localize);
        ArgumentNullException.ThrowIfNull(language);
        _l = localize;
        _language = language;
        HeroPanel = CreateHeroPanel(out _heroTitle, out _characterLevel, out _healthTitle,
            out _health, out _experience, out _damageTitle, out _damage, out _fireRateTitle,
            out _fireRate, out _shotsTitle, out _shots, out _tempo, out _weapon,
            out _companions, out _reward, out _powerup, out _negative, out _penalty, out _pickup);
        StageHeader = CreateStageHeader(out _gameLevel, out _stageTime, out _stageProgress, out _boss, out _bossHealth);
        Abilities = CreateAbilities(out _speedUp, out _slowDown, out _super, out _timeScope, out _timeEffect);
        Update(new LivingArcadeSnapshot());
    }

    public void Update(LivingArcadeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        SetText(_heroTitle, _l("Arcade.Hud.Hero"));
        SetText(_healthTitle, _l("Arcade.Health"));
        SetText(_characterLevel, Format("Arcade.Hud.CharacterLevel", Math.Max(1, snapshot.CharacterLevel)));
        var health = Positive(snapshot.Health);
        var maxHealth = Positive(snapshot.MaxHealth);
        var healthNumbers = $"{Math.Ceiling(health):0}/{Math.Ceiling(maxHealth):0}";
        _health.Update(Ratio(health, maxHealth), "HP " + healthNumbers,
            _l("Arcade.Health") + " " + healthNumbers, healthNumbers);
        var kills = LivingArcadeHud.KillCount(Math.Max(0, snapshot.KillsTowardNextLevel));
        var required = LivingArcadeHud.KillCount(Math.Max(0, snapshot.KillsRequiredForNextLevel));
        var experienceNumbers = kills + "/" + required;
        var longExperience = experienceNumbers.Length > 34;
        _experience.Surface.Height = longExperience ? 34 : 22;
        _experience.Update(Ratio(Math.Max(0, snapshot.KillsTowardNextLevel), Math.Max(0, snapshot.KillsRequiredForNextLevel)),
            longExperience ? kills + " /\n" + required : experienceNumbers,
            _l("Arcade.Hud.Experience") + " " + experienceNumbers, experienceNumbers);
        SetText(_damageTitle, _l("Arcade.Hud.Damage"));
        SetText(_fireRateTitle, _l("Arcade.Hud.FireRate"));
        SetText(_shotsTitle, _l("Arcade.Hud.Shots"));
        SetText(_damage, Number(snapshot.ProjectileDamage));
        SetText(_fireRate, Number(snapshot.FireRate) + "/" + _l("Arcade.SecondsShort"));
        SetText(_shots, Math.Max(0, snapshot.SpreadShots).ToString(CultureInfo.CurrentCulture));
        AutomationProperties.SetName(_damage, _damageTitle.Text + ": " + _damage.Text);
        AutomationProperties.SetName(_fireRate, _fireRateTitle.Text + ": " + _fireRate.Text);
        AutomationProperties.SetName(_shots, _shotsTitle.Text + ": " + _shots.Text);
        SetText(_tempo, Format("Arcade.Tempo", Positive(snapshot.TempoMultiplier)));
        SetText(_weapon, LivingArcadeHud.Weapon(_l, snapshot.WeaponKind, Positive(snapshot.WeaponSeconds)));
        Optional(_companions, LivingArcadeHud.Companions(_l, snapshot.Companions));
        if (snapshot.BossReward is { RemainingLevels: > 0 } active)
        {
            Optional(_reward, LivingArcadeHud.BossReward(_l, active, _language()));
            var description = _language().StartsWith("en", StringComparison.OrdinalIgnoreCase)
                ? active.Reward.DescriptionEn : active.Reward.DescriptionRu;
            _reward.ToolTip = _reward.Text + Environment.NewLine + LivingArcadeHud.BossRewardDetails(_l, active.Reward)
                + Environment.NewLine + Environment.NewLine + _l("Arcade.BossReward.ModelDescription")
                + Environment.NewLine + description;
        }
        else Optional(_reward, "");
        Optional(_powerup, snapshot.PowerupSeconds > 0 && !string.IsNullOrEmpty(snapshot.PowerupEffect)
            ? LivingArcadeHud.TimedEffect(_l, snapshot.PowerupEffect, Positive(snapshot.PowerupSeconds)) : "");
        Optional(_negative, snapshot.NegativeSeconds > 0 && !string.IsNullOrEmpty(snapshot.NegativeEffect)
            ? LivingArcadeHud.TimedEffect(_l, snapshot.NegativeEffect, Positive(snapshot.NegativeSeconds)) : "");
        Optional(_penalty, !string.IsNullOrEmpty(snapshot.Penalty) && snapshot.PenaltyUntil > snapshot.Progress
            ? _l("Arcade.Penalty." + snapshot.Penalty) + " · " + Math.Ceiling(Positive(snapshot.PenaltyUntil - snapshot.Progress)).ToString("0")
                + " " + _l("Arcade.SecondsShort") : "");
        Optional(_pickup, snapshot.RecentBonus is { } bonus && snapshot.BonusFeedbackSeconds > 0
            ? LivingArcadeHud.Pickup(_l, bonus) : "");

        SetText(_gameLevel, Format("Arcade.Hud.GameLevel", Math.Max(1, snapshot.GameLevel)));
        var duration = Positive(snapshot.DurationSeconds);
        var progress = Math.Min(Positive(snapshot.Progress), duration);
        SetText(_stageTime, $"{progress:0} / {duration:0} " + _l("Arcade.SecondsShort"));
        var stageName = _gameLevel.Text + " · " + _stageTime.Text;
        _stageProgress.Update(Ratio(progress, duration), "", stageName, _stageTime.Text);
        AutomationProperties.SetName(StageHeader, stageName);
        var hasBoss = snapshot.BossPhase > 0 && Positive(snapshot.BossMaxHealth) > 0;
        Optional(_boss, hasBoss ? Format("Arcade.BossStats", snapshot.BossPhase,
            Math.Ceiling(Positive(snapshot.BossHealth)), Math.Ceiling(Positive(snapshot.BossMaxHealth))) : "");
        _bossHealth.Surface.Visibility = hasBoss ? Visibility.Visible : Visibility.Collapsed;
        _bossHealth.Update(Ratio(snapshot.BossHealth, snapshot.BossMaxHealth), "", _boss.Text,
            $"{Math.Ceiling(Positive(snapshot.BossHealth)):0}/{Math.Ceiling(Positive(snapshot.BossMaxHealth)):0}");

        UpdateAbility(_speedUp, "Arcade.Hud.SpeedUp", snapshot.SpeedUpCooldown);
        UpdateAbility(_slowDown, "Arcade.Hud.SlowDown", snapshot.SlowDownCooldown);
        UpdateAbility(_super, "Arcade.Hud.Super", snapshot.SuperCooldown);
        SetText(_timeScope, _l(snapshot.SelectiveTime ? "Arcade.TimeScope.Selective" : "Arcade.TimeScope.World"));
        Optional(_timeEffect, !string.IsNullOrEmpty(snapshot.TimeEffect) && snapshot.EffectSeconds > 0
            ? _l("Arcade.TimeEffect." + snapshot.TimeEffect) + " · " + Math.Ceiling(Positive(snapshot.EffectSeconds)).ToString("0")
                + " " + _l("Arcade.SecondsShort") : "");
        AutomationProperties.SetName(HeroPanel, _l("Arcade.Hud.Hero") + " · " + _characterLevel.Text);
        AutomationProperties.SetName(Abilities, _l("Arcade.Hud.Abilities"));
    }

    private void UpdateAbility(Ability ability, string titleKey, double cooldown)
    {
        var ready = Positive(cooldown) <= 0;
        SetText(ability.Title, _l(titleKey));
        SetText(ability.Cooldown, ready ? _l("Arcade.AbilityReady")
            : Math.Ceiling(Positive(cooldown)).ToString("0") + " " + _l("Arcade.SecondsShort"));
        ability.Cooldown.Foreground = ready ? Cyan : Muted;
        ability.Card.BorderBrush = ready ? ReadyEdge : Edge;
        AutomationProperties.SetName(ability.Card, ability.Key + " · " + ability.Title.Text + " · " + ability.Cooldown.Text);
        AutomationProperties.SetHelpText(ability.Card, _l("Arcade.Hud.KeyboardAbility"));
    }

    private string Format(string key, params object[] values) => string.Format(CultureInfo.CurrentCulture, _l(key), values);
    private static double Positive(double value) => double.IsFinite(value) ? Math.Max(0, value) : 0;
    private static double Ratio(double current, double maximum) => maximum > 0 && double.IsFinite(maximum)
        ? Math.Clamp(Positive(current) / maximum, 0, 1) : 0;
    private static string Number(double value) => Positive(value).ToString("0.##", CultureInfo.CurrentCulture);
}
