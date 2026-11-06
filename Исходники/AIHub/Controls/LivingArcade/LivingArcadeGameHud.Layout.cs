using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace AIHub.Controls.LivingArcade;

public sealed partial class LivingArcadeGameHud
{
    private static FrameworkElement CreateHeroPanel(out TextBlock title, out TextBlock characterLevel,
        out TextBlock healthTitle, out Meter health, out Meter experience,
        out TextBlock damageTitle, out TextBlock damage, out TextBlock fireRateTitle, out TextBlock fireRate,
        out TextBlock shotsTitle, out TextBlock shots, out TextBlock tempo, out TextBlock weapon,
        out TextBlock companions, out TextBlock reward, out TextBlock powerup, out TextBlock negative,
        out TextBlock penalty, out TextBlock pickup)
    {
        var content = new StackPanel();
        var heading = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        heading.ColumnDefinitions.Add(new());
        heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        title = Text("HeroTitle", 12, Muted, FontWeights.Bold);
        heading.Children.Add(title);
        characterLevel = Text("CharacterLevel", 12, Cyan, FontWeights.Bold);
        Grid.SetColumn(characterLevel, 1);
        heading.Children.Add(characterLevel);
        content.Children.Add(heading);
        healthTitle = Text("HealthTitle", 10, Muted);
        healthTitle.Margin = new Thickness(0, 0, 0, 4);
        content.Children.Add(healthTitle);
        health = new Meter("HealthBar", "Health", Red, 28, 14, FontWeights.Bold);
        content.Children.Add(health.Surface);
        experience = new Meter("ExperienceBar", "Experience", Blue, 22, 11, FontWeights.SemiBold);
        experience.Surface.Margin = new Thickness(0, 6, 0, 10);
        content.Children.Add(experience.Surface);

        var stats = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        for (var i = 0; i < 3; i++) stats.ColumnDefinitions.Add(new());
        stats.Children.Add(Stat("DamageTitle", "Damage", 0, out damageTitle, out damage));
        stats.Children.Add(Stat("FireRateTitle", "FireRate", 1, out fireRateTitle, out fireRate));
        stats.Children.Add(Stat("ShotsTitle", "Shots", 2, out shotsTitle, out shots));
        content.Children.Add(stats);
        tempo = Text("Tempo", 11, Muted);
        content.Children.Add(tempo);
        content.Children.Add(Divider());
        weapon = Text("Weapon", 13, Foreground, FontWeights.SemiBold, true);
        content.Children.Add(weapon);
        companions = Detail("Companions", Cyan);
        content.Children.Add(companions);
        reward = Detail("BossReward", Gold, 3);
        content.Children.Add(reward);
        powerup = Detail("Powerup", Cyan);
        content.Children.Add(powerup);
        negative = Detail("NegativeEffect", Warning);
        content.Children.Add(negative);
        penalty = Detail("Penalty", Warning);
        content.Children.Add(penalty);
        pickup = Detail("Pickup", Foreground, 3);
        pickup.Margin = new Thickness(0, 8, 0, 0);
        content.Children.Add(pickup);
        var panel = Panel("HeroPanel", content, new Thickness(12));
        panel.Width = 248;
        panel.MaxWidth = 260;
        panel.VerticalAlignment = VerticalAlignment.Top;
        return panel;
    }

    private static FrameworkElement CreateStageHeader(out TextBlock gameLevel, out TextBlock stageTime,
        out Meter progress, out TextBlock boss, out Meter bossHealth)
    {
        var content = new StackPanel();
        var heading = new Grid();
        heading.ColumnDefinitions.Add(new());
        heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        gameLevel = Text("GameLevel", 17, Foreground, FontWeights.Bold);
        heading.Children.Add(gameLevel);
        stageTime = Text("StageTime", 13, Muted, FontWeights.SemiBold);
        stageTime.Margin = new Thickness(18, 3, 0, 0);
        Grid.SetColumn(stageTime, 1);
        heading.Children.Add(stageTime);
        content.Children.Add(heading);
        progress = new Meter("StageProgressBar", "StageProgress", Cyan, 4, 1, FontWeights.Normal);
        progress.Surface.Margin = new Thickness(0, 8, 0, 0);
        content.Children.Add(progress.Surface);
        boss = Text("Boss", 11, Gold, FontWeights.SemiBold, true);
        boss.Margin = new Thickness(0, 8, 0, 0);
        boss.TextAlignment = TextAlignment.Center;
        content.Children.Add(boss);
        bossHealth = new Meter("BossBar", "BossHealth", Red, 7, 1, FontWeights.Normal);
        bossHealth.Surface.Margin = new Thickness(0, 4, 0, 0);
        content.Children.Add(bossHealth.Surface);
        var panel = Panel("StageHeader", content, new Thickness(14, 9, 14, 10));
        panel.Width = 340;
        panel.MaxWidth = 380;
        panel.HorizontalAlignment = HorizontalAlignment.Center;
        panel.VerticalAlignment = VerticalAlignment.Top;
        return panel;
    }

    private static FrameworkElement CreateAbilities(out Ability speedUp, out Ability slowDown, out Ability super,
        out TextBlock scope, out TextBlock effect)
    {
        var content = new StackPanel();
        var row = new Grid();
        for (var i = 0; i < 3; i++) row.ColumnDefinitions.Add(new());
        speedUp = new Ability("SpeedUp", "W ↑");
        slowDown = new Ability("SlowDown", "S ↓");
        super = new Ability("Super", "F / Num 0");
        var abilities = new[] { speedUp, slowDown, super };
        for (var i = 0; i < abilities.Length; i++)
        {
            Grid.SetColumn(abilities[i].Card, i);
            row.Children.Add(abilities[i].Card);
        }
        content.Children.Add(row);
        scope = Text("TimeScope", 10, Muted);
        scope.Margin = new Thickness(0, 5, 0, 0);
        scope.TextAlignment = TextAlignment.Center;
        content.Children.Add(scope);
        effect = Text("TimeEffect", 11, Cyan, FontWeights.SemiBold);
        effect.Margin = new Thickness(0, 3, 0, 0);
        effect.TextAlignment = TextAlignment.Center;
        content.Children.Add(effect);
        var panel = Panel("Abilities", content, new Thickness(8, 8, 8, 7));
        panel.Width = 330;
        panel.HorizontalAlignment = HorizontalAlignment.Center;
        panel.VerticalAlignment = VerticalAlignment.Bottom;
        return panel;
    }

    private static StackPanel Stat(string titleId, string valueId, int column, out TextBlock title, out TextBlock value)
    {
        var panel = new StackPanel();
        title = Text(titleId, 10, Muted);
        value = Text(valueId, 16, Foreground, FontWeights.SemiBold);
        value.Margin = new Thickness(0, 2, 0, 0);
        panel.Children.Add(title);
        panel.Children.Add(value);
        Grid.SetColumn(panel, column);
        return panel;
    }

    private sealed class Ability
    {
        internal string Key { get; }
        internal Border Card { get; }
        internal TextBlock Title { get; }
        internal TextBlock Cooldown { get; }

        internal Ability(string id, string key)
        {
            Key = key;
            var content = new StackPanel();
            var keyText = Text(id + "Key", 14, Foreground, FontWeights.Bold);
            keyText.Text = key;
            keyText.TextAlignment = TextAlignment.Center;
            content.Children.Add(keyText);
            Title = Text(id + "Title", 10, Muted);
            Title.TextAlignment = TextAlignment.Center;
            Title.Margin = new Thickness(0, 3, 0, 3);
            content.Children.Add(Title);
            Cooldown = Text(id + "Cooldown", 11, Cyan, FontWeights.SemiBold);
            Cooldown.TextAlignment = TextAlignment.Center;
            content.Children.Add(Cooldown);
            Card = Panel(id, content, new Thickness(4, 6, 4, 5));
            Card.Background = Well;
            Card.CornerRadius = new CornerRadius(5);
            Card.Margin = new Thickness(3, 0, 3, 0);
        }
    }
}
