using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Markup;
using System.Xml.Linq;
using AIHub.Controls;
using AIHub.Controls.LivingArcade;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.LivingArcade;
using Button = System.Windows.Controls.Button;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class LivingArcadeGameHudTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("ru")]
    [DataRow("en")]
    public Task BarsAndCooldownsFollowTheLatestSnapshotAndExpiredEffectsDisappear(string language) => ScenarioNavigationTests.Sta(() =>
    {
        var localizer = new LocalizationService(); localizer.Load(language);
        var hud = new LivingArcadeGameHud(localizer.T, () => language);
        var panel = new StackPanel();
        panel.Children.Add(hud.StageHeader); panel.Children.Add(hud.HeroPanel); panel.Children.Add(hud.Abilities);
        var window = WindowFor(panel, 1024, 900);
        try
        {
            hud.Update(BusySnapshot()); window.Show(); window.UpdateLayout();
            StringAssert.Contains(Element<TextBlock>(hud.HeroPanel, "Health").Text, "38/120");
            StringAssert.Contains(Element<TextBlock>(hud.HeroPanel, "Experience").Text, "1 242/10 000");
            StringAssert.Contains(Element<TextBlock>(hud.StageHeader, "StageTime").Text.Replace(" ", ""), "120/120");
            AssertFill(hud.HeroPanel, "HealthBar", 37.2 / 120);
            AssertFill(hud.HeroPanel, "ExperienceBar", 1242d / 10000);
            AssertFill(hud.StageHeader, "StageProgressBar", 1);
            AssertFill(hud.StageHeader, "BossBar", 51.4 / 90);
            StringAssert.Contains(Element<TextBlock>(hud.Abilities, "SpeedUpCooldown").Text, "2");
            StringAssert.Contains(Element<TextBlock>(hud.Abilities, "SlowDownCooldown").Text, localizer.T("Arcade.AbilityReady"));
            StringAssert.Contains(Element<TextBlock>(hud.Abilities, "SuperCooldown").Text, "118");
            StringAssert.Contains(Element<TextBlock>(hud.HeroPanel, "Powerup").Text, localizer.T("Arcade.Powerup.spread"));
            StringAssert.Contains(Element<TextBlock>(hud.HeroPanel, "NegativeEffect").Text, localizer.T("Arcade.Powerup.drag"));

            hud.Update(new LivingArcadeSnapshot { Health = 20, MaxHealth = 80, KillsTowardNextLevel = 5,
                KillsRequiredForNextLevel = 10, Progress = 30, DurationSeconds = 120 });
            window.UpdateLayout();
            AssertFill(hud.HeroPanel, "HealthBar", .25);
            AssertFill(hud.HeroPanel, "ExperienceBar", .5);
            AssertFill(hud.StageHeader, "StageProgressBar", .25);
            foreach (var name in new[] { "Powerup", "NegativeEffect", "Penalty", "Pickup", "BossReward" })
            {
                var expired = Element<FrameworkElement>(hud.HeroPanel, name);
                Assert.AreEqual(Visibility.Collapsed, expired.Visibility, name + " must not leave stale status behind.");
                Assert.IsTrue(expired.ToolTip is null or "", name + " must not leave a stale tooltip.");
            }
            Assert.AreEqual(Visibility.Collapsed, Element<FrameworkElement>(hud.StageHeader, "BossBar").Visibility);
            Assert.AreEqual(Visibility.Collapsed, Element<FrameworkElement>(hud.StageHeader, "Boss").Visibility);
            var timeEffect = Element<FrameworkElement>(hud.Abilities, "TimeEffect");
            Assert.AreEqual(Visibility.Collapsed, timeEffect.Visibility);
            Assert.IsTrue(timeEffect.ToolTip is null or "", "The expired time mode must not leave a stale tooltip.");
            foreach (var name in new[] { "SpeedUpCooldown", "SlowDownCooldown", "SuperCooldown" })
                Assert.AreEqual(localizer.T("Arcade.AbilityReady"), Element<TextBlock>(hud.Abilities, name).Text);
        }
        finally { window.Close(); }
    });

    [TestMethod]
    [DataRow("ru", 1440, 900)]
    [DataRow("en", 1440, 900)]
    [DataRow("ru", 1024, 700)]
    [DataRow("en", 1024, 700)]
    public Task GameplayKeepsReadablePanelsOutsideTheRoadAtWideAndCompactSizes(string language, int width, int height) => ScenarioNavigationTests.Sta(() =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "arcade-hud-layout-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LivingArcadeRunStore(directory);
            var localizer = new LocalizationService(); localizer.Load(language);
            using var generation = new LivingArcadeGeneration(new UserContextService(new(), new()));
            using var page = new LivingArcadeControl(localizer.T, store, generation,
                new("unused", "unused"), () => language, default);
            StartFixtureRun(page, store);
            Frame(page, BusySnapshot());
            var window = WindowFor(page, width, height);
            try
            {
                window.Show(); window.UpdateLayout();
                var host = Descendants(page).OfType<LivingArcadeGameHost>().Single(); host.Stop();
                Frame(page, BusySnapshot()); window.UpdateLayout();
                var hud = Hud(page);
                Assert.IsTrue(hud.HeroPanel.IsVisible && hud.StageHeader.IsVisible && hud.Abilities.IsVisible,
                    "The visible gameplay tree must contain all three HUD regions.");
                var road = Bounds(host, page);
                var hero = Bounds(hud.HeroPanel, page);
                var stage = Bounds(hud.StageHeader, page);
                var abilities = Bounds(hud.Abilities, page);
                Assert.IsTrue(host.ActualWidth >= 300 && host.ActualHeight >= height * .6,
                    "The road must retain enough room for gameplay.");
                Assert.IsTrue(hero.Right <= road.Left + 1, "The hero card must stay outside the road.");
                Assert.IsTrue(stage.Bottom <= road.Top + 1, "The stage header must stay above the road.");
                Assert.IsTrue(abilities.Top >= road.Bottom - 1, "Abilities must stay below the road.");
                var actions = Descendants(page).OfType<FrameworkElement>().Single(element =>
                    AutomationProperties.GetAutomationId(element) == "Arcade.Game.Actions");
                var sound = Descendants(page).OfType<FrameworkElement>().Single(element =>
                    AutomationProperties.GetAutomationId(element) == "Arcade.Game.Sound");
                Assert.IsTrue(Bounds(actions, page).Right <= abilities.Left + 1, "Action icons must not cover ability cards.");
                Assert.IsTrue(abilities.Right <= Bounds(sound, page).Left + 1, "Sound sliders must not cover ability cards.");
                foreach (var region in new[] { hud.HeroPanel, hud.StageHeader, hud.Abilities })
                {
                    var bounds = Bounds(region, page);
                    Assert.IsTrue(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= page.ActualWidth + 1
                        && bounds.Bottom <= page.ActualHeight + 1, "Every HUD region must stay in the viewport.");
                    AssertTextDoesNotOverlap(region, page);
                }
                foreach (var name in new[] { "HeroTitle", "HealthTitle", "Health", "Experience", "CharacterLevel", "DamageTitle",
                    "Damage", "FireRateTitle", "FireRate", "ShotsTitle", "Shots", "Tempo", "Weapon" })
                    AssertTextFits(Element<TextBlock>(hud.HeroPanel, name));
                foreach (var name in new[] { "GameLevel", "StageTime", "Boss" })
                    AssertTextFits(Element<TextBlock>(hud.StageHeader, name));
                foreach (var name in new[] { "SpeedUpKey", "SpeedUpTitle", "SpeedUpCooldown", "SlowDownKey", "SlowDownTitle",
                    "SlowDownCooldown", "SuperKey", "SuperTitle", "SuperCooldown" })
                    AssertTextFits(Element<TextBlock>(hud.Abilities, name));
                Assert.IsFalse(Text(hud.HeroPanel).Contains("Arcade.", StringComparison.Ordinal));
                Assert.IsFalse(Text(hud.StageHeader).Contains("Arcade.", StringComparison.Ordinal));
                Assert.IsFalse(Text(hud.Abilities).Contains("Arcade.", StringComparison.Ordinal));
                var summary = (FrameworkElement)typeof(LivingArcadeControl).GetField("_hud", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!;
                Assert.AreEqual(Visibility.Collapsed, summary.Visibility, "Gameplay must not display the old giant summary.");
                SaveFrame(page, $"living-arcade-hud-{language}-{width}x{height}.png");
            }
            finally { window.Close(); }
        }
        finally { DeleteFixture(directory); }
    });

    [TestMethod]
    [DataRow("ru")]
    [DataRow("en")]
    public Task LongExperienceValuesStayReadableInTheCompactHeroCard(string language) => ScenarioNavigationTests.Sta(() =>
    {
        var localizer = new LocalizationService(); localizer.Load(language);
        var hud = new LivingArcadeGameHud(localizer.T, () => language);
        hud.HeroPanel.Width = 230;
        hud.Update(new() { KillsTowardNextLevel = long.MaxValue / 2, KillsRequiredForNextLevel = long.MaxValue });
        var window = WindowFor(new Border { Width = 230, Child = hud.HeroPanel }, 260, 700);
        try
        {
            window.Show(); window.UpdateLayout();
            var experience = Element<TextBlock>(hud.HeroPanel, "Experience");
            var plainText = experience.Text.Replace("\r", "").Replace("\n", "").Replace(" ", "");
            Assert.AreEqual($"{long.MaxValue / 2}/{long.MaxValue}", plainText);
            AssertTextFits(experience);
            AssertFill(hud.HeroPanel, "ExperienceBar", .5);
        }
        finally { window.Close(); }
    });

    [TestMethod]
    [DataRow("ru")]
    [DataRow("en")]
    public Task FooterIconsControlTheGameAndSoundSurvivesMenuReentry(string language) => ScenarioNavigationTests.Sta(() =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "arcade-hud-layout-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LivingArcadeRunStore(directory);
            var localizer = new LocalizationService(); localizer.Load(language);
            using var generation = new LivingArcadeGeneration(new UserContextService(new(), new()));
            using var page = new LivingArcadeControl(localizer.T, store, generation,
                new("unused", "unused"), () => language, default);
            StartFixtureRun(page, store);
            var runId = store.List().Single().Id;
            var window = WindowFor(page, 1024, 700);
            try
            {
                window.Show(); window.UpdateLayout();
                var host = Descendants(page).OfType<LivingArcadeGameHost>().Single();
                T Visible<T>(string id) where T : FrameworkElement => Descendants(page).OfType<T>()
                    .Single(element => element.IsVisible && AutomationProperties.GetAutomationId(element) == id);
                foreach (var id in new[] { "Arcade.PauseGame", "Arcade.ResumeGame", "Arcade.PauseGeneration" })
                {
                    var icon = Visible<Button>(id);
                    Assert.IsInstanceOfType(icon.Content, typeof(FrameworkElement), id + " must display a graphical icon.");
                    Assert.AreEqual(localizer.T(id), icon.ToolTip);
                    Assert.AreEqual(localizer.T(id), AutomationProperties.GetName(icon));
                }
                var pause = Visible<Button>("Arcade.PauseGame");
                var resume = Visible<Button>("Arcade.ResumeGame");
                bool PagePaused() => (bool)typeof(LivingArcadeControl).GetField("_paused", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!;
                void AssertPauseState(bool expected)
                {
                    Assert.AreEqual(expected, PagePaused(), "The page must retain the actual pause state.");
                    Assert.AreEqual(expected, host.Snapshot!.IsPaused, "The simulation must match the page's pause state.");
                    Assert.AreEqual(!expected, host.IsRunning);
                    Assert.AreEqual(!expected, pause.IsEnabled);
                    Assert.AreEqual(expected, resume.IsEnabled);
                }
                TestContext.WriteLine($"After Show: pagePaused={PagePaused()}, running={host.IsRunning}, windowActive={window.IsActive}, fieldKeyboardFocus={host.IsKeyboardFocusWithin}");
                AssertPauseState(PagePaused());
                // An offscreen window may already have paused itself on focus loss while Show was arranging it.
                if (PagePaused()) resume.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                AssertPauseState(false);
                pause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                AssertPauseState(true);
                resume.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                AssertPauseState(false);
                System.Windows.Input.Keyboard.ClearFocus();
                Assert.IsFalse(host.IsKeyboardFocusWithin, "The field must really have lost keyboard focus.");
                typeof(LivingArcadeGameHost).GetMethod("RenderFrame", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(host, [null, EventArgs.Empty]);
                AssertPauseState(true);
                pause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                AssertPauseState(true); // A Pause click after focus loss must never resume the field.
                resume.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                AssertPauseState(false);

                Visible<Slider>("Arcade.EffectsVolume").Value = .27;
                Visible<Slider>("Arcade.MusicVolume").Value = .63;
                Assert.AreEqual(.27, host.Audio.EffectsVolume, .0001);
                Assert.AreEqual(.63, host.Audio.MusicVolume, .0001);
                Visible<Button>("Arcade.ToMenu").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
                Assert.AreEqual(.27, Visible<Slider>("Arcade.EffectsVolume").Value, .0001);
                Assert.AreEqual(.63, Visible<Slider>("Arcade.MusicVolume").Value, .0001);
                Visible<Button>("Arcade.Continue." + runId).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
                var restarted = Descendants(page).OfType<LivingArcadeGameHost>().Single(); restarted.Stop();
                Assert.AreNotSame(host, restarted);
                Assert.IsTrue(Hud(page).HeroPanel.IsVisible && Hud(page).Abilities.IsVisible && Hud(page).StageHeader.IsVisible,
                    "Reentry must attach the existing HUD regions to the new field without duplicate-parent errors.");
                Assert.AreEqual(.27, restarted.Audio.EffectsVolume, .0001);
                Assert.AreEqual(.63, restarted.Audio.MusicVolume, .0001);
                Assert.AreEqual(1, Descendants(page).OfType<FrameworkElement>().Count(element => element.IsVisible
                    && AutomationProperties.GetAutomationId(element) == "Arcade.Game.Surface"));
            }
            finally { window.Close(); }
        }
        finally { DeleteFixture(directory); }
    });

    internal static LivingArcadeGameHud Hud(LivingArcadeControl page) =>
        (LivingArcadeGameHud)typeof(LivingArcadeControl).GetField("_gameHud", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;

    internal static T Element<T>(DependencyObject root, string name) where T : DependencyObject =>
        Descendants(root).OfType<T>().Single(element => AutomationProperties.GetAutomationId(element) == "Arcade.Hud." + name);

    internal static string Text(DependencyObject root) => string.Join("\n", Descendants(root).OfType<TextBlock>()
        .Where(text => text.Visibility == Visibility.Visible).Select(text => text.Text));

    internal static void StartFixtureRun(LivingArcadeControl page, LivingArcadeRunStore store)
    {
        var run = store.Create("HUD layout fixture", 37);
        store.Update(run.Id, value => value.FirstEventsPrepared = true);
        var type = typeof(LivingArcadeControl);
        void Set(string name, object value) => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);
        Set("_ready", true); Set("_generating", true); Set("_musicVolume", 0d); Set("_effectsVolume", 0d);
        type.GetMethod("StartRun", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, [run.Id]);
        var host = (LivingArcadeGameHost)type.GetField("_host", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
        host.Stop();
        Set("_lastHud", DateTime.MinValue);
    }

    internal static void Frame(LivingArcadeControl page, LivingArcadeSnapshot snapshot)
    {
        typeof(LivingArcadeControl).GetField("_lastHud", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, DateTime.MinValue);
        typeof(LivingArcadeControl).GetMethod("OnGameFrame", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, [snapshot]);
    }

    private static LivingArcadeSnapshot BusySnapshot() => new()
    {
        Health = 37.2, MaxHealth = 120, GameLevel = 10, CharacterLevel = 15,
        KillsTowardNextLevel = 1242, KillsRequiredForNextLevel = 10000,
        Progress = 140, DurationSeconds = 120, ProjectileDamage = 3.25, FireRate = 7.5, SpreadShots = 3, TempoMultiplier = 1.15,
        SpeedUpCooldown = 1.1, SlowDownCooldown = 0, SuperCooldown = 117.1,
        TimeEffect = "speed_up", EffectSeconds = 11.1, SelectiveTime = true,
        WeaponKind = "laser", WeaponSeconds = 5.1, PowerupEffect = "spread", PowerupSeconds = 4.2,
        NegativeEffect = "drag", NegativeSeconds = 2.1, Penalty = "weak", PenaltyUntil = 170,
        BossPhase = 2, BossHealth = 51.4, BossMaxHealth = 90,
        Companions = [new("helper1", .4, .7, .02, "#66CCFF", "triangle", .5, false) { Health = 7, MaxHealth = 14 },
            new("helper2", .6, .7, .02, "#66CCFF", "triangle", .75, false) { Health = 18, MaxHealth = 24 }],
        BossReward = new() { RemainingLevels = 2, Reward = new() { TitleRu = "Косой веер", TitleEn = "Side fan",
            DescriptionRu = "Модель обещает пять снарядов, хотя исполняемый веер даёт три.",
            DescriptionEn = "The model claims five projectiles although the executable fan gives three.",
            DurationLevels = 2, DamageMultiplier = 1.5, FireRateMultiplier = 1.25, ShotPattern = "fan" } },
        RecentBonus = new(.02, 0, 0, 0, false) { Kind = "weapon" }, BonusFeedbackSeconds = 1.5
    };

    internal static Window WindowFor(object content, double width, double height)
    {
        // Read the real shell resources without constructing MainWindow or running application startup.
        var shell = XDocument.Load(MainWindowSource()).Root!;
        var resources = new XElement(shell.Name.Namespace + "ResourceDictionary",
            shell.Attributes().Where(attribute => attribute.IsNamespaceDeclaration),
            shell.Element(shell.Name.Namespace + "Window.Resources")!.Elements().Select(element => new XElement(element)));
        var window = new Window
        {
            Width = width, Height = height, Left = -10000, Top = -10000, ShowActivated = false,
            ShowInTaskbar = false, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            FontFamily = new System.Windows.Media.FontFamily((string)shell.Attribute("FontFamily")!),
            UseLayoutRounding = (bool?)shell.Attribute("UseLayoutRounding") ?? false,
            Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString(SaveOptions.DisableFormatting))
        };
        window.SetResourceReference(Window.BackgroundProperty, "WindowBackgroundBrush");
        window.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextPrimaryBrush");
        window.SetResourceReference(System.Windows.Controls.Control.FontSizeProperty, "UiBodyFontSize");
        window.Content = content;
        return window;
    }

    private static string MainWindowSource([CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", "AIHub", "MainWindow.xaml"));

    private static void AssertFill(DependencyObject root, string name, double fraction)
    {
        var track = Element<FrameworkElement>(root, name);
        var fill = Element<FrameworkElement>(root, name + ".Fill");
        Assert.IsTrue(track.ActualWidth > 10, name + " must have a visible width.");
        var ratio = fill.ActualWidth / track.ActualWidth;
        Assert.AreEqual(fraction, ratio, .025, name + " must paint the real snapshot fraction.");
    }

    private static Rect Bounds(FrameworkElement element, Visual relativeTo) =>
        element.TransformToAncestor(relativeTo).TransformBounds(new Rect(new Point(), element.RenderSize));

    private static void AssertTextFits(TextBlock text)
    {
        Assert.IsTrue(text.IsVisible && text.ActualWidth > 0 && text.ActualHeight > 0,
            AutomationProperties.GetAutomationId(text) + " must actually be on screen.");
        var formatted = new FormattedText(text.Text, System.Globalization.CultureInfo.CurrentUICulture, text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize,
            text.Foreground ?? Brushes.White, VisualTreeHelper.GetDpi(text).PixelsPerDip);
        if (text.TextWrapping != TextWrapping.NoWrap) formatted.MaxTextWidth = Math.Max(1, text.ActualWidth - text.Padding.Left - text.Padding.Right);
        Assert.IsTrue(formatted.WidthIncludingTrailingWhitespace <= text.ActualWidth + 2
            && formatted.Height <= text.ActualHeight + 2,
            $"{AutomationProperties.GetAutomationId(text)} clips its visible text '{text.Text}' ({formatted.WidthIncludingTrailingWhitespace:0.0}×{formatted.Height:0.0} in {text.ActualWidth:0.0}×{text.ActualHeight:0.0}).");
    }

    private static void AssertTextDoesNotOverlap(DependencyObject region, Visual relativeTo)
    {
        var text = Descendants(region).OfType<TextBlock>().Where(t => t.IsVisible && t.Text.Length > 0).ToArray();
        for (var first = 0; first < text.Length; first++)
        for (var second = first + 1; second < text.Length; second++)
        {
            var intersection = Rect.Intersect(Bounds(text[first], relativeTo), Bounds(text[second], relativeTo));
            Assert.IsTrue(intersection.IsEmpty || intersection.Width <= 1 || intersection.Height <= 1,
                $"Visible text overlaps: {AutomationProperties.GetAutomationId(text[first])} and {AutomationProperties.GetAutomationId(text[second])}.");
        }
    }

    private void SaveFrame(FrameworkElement element, string name)
    {
        var directory = Path.Combine(Path.GetTempPath(), "lopata-living-arcade-hud-render"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var output = File.Create(path)) encoder.Save(output);
        TestContext.AddResultFile(path);
    }

    private static void DeleteFixture(string directory)
    {
        var full = Path.GetFullPath(directory);
        if (Directory.Exists(full) && full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(full).StartsWith("arcade-hud-layout-", StringComparison.Ordinal)) Directory.Delete(full, true);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in Descendants(child)) yield return descendant;
    }
}
