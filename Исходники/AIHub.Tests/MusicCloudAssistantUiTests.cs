using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using MenuItem = System.Windows.Controls.MenuItem;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicCloudAssistantUiTests
{
    [TestMethod]
    [DataRow("ru", true)]
    [DataRow("en", true)]
    [DataRow("ru", false)]
    [DataRow("en", false)]
    public Task ChoiceShowsScoresAndLanguageRecommendationWithoutSavingDefault(string language, bool dark) => ScenarioNavigationTests.Sta(() =>
    {
        var folder = Path.Combine(Path.GetTempPath(), "lopata-cloud-ui-" + Guid.NewGuid().ToString("N"));
        var preferences = new MusicPoetryPreferences(Path.Combine(folder, "prefs.json"));
        var localizer = new LocalizationService(); localizer.Load(language);
        var editor = new MusicPoetryWindow(() => new(), new(), new(new UserProfileStore(), new IpLocationService()),
            localizer.T, language, new(Path.Combine(folder, "sessions")), preferences: preferences, modelChoices: () => []);
        var theme = ScenarioNavigationTests.Create(language, 1, dark).Resources;
        foreach (var key in new[] { "TextPrimaryBrush", "TextSecondaryBrush", "PanelBrush", "LineBrush", "AccentBrush", "UiBodyFontSize" })
            editor.Resources[key] = theme[key];
        editor.Resources["WindowBackgroundBrush"] = dark ? System.Windows.Media.Brushes.MidnightBlue : System.Windows.Media.Brushes.WhiteSmoke;
        editor.Resources["SecondaryButtonBackgroundBrush"] = theme["PanelBrush"];
        try
        {
            editor.Show(); Call(editor, "CancelModelEntry");
            Exception? failure = null;
            editor.Dispatcher.BeginInvoke(new Action(() => {
                var dialog = editor.OwnedWindows.Cast<Window>().Single(w => AutomationProperties.GetAutomationId(w) == "Music.Poetry.Cloud.Choose");
                try
                {
                    dialog.UpdateLayout();
                    var controls = ScenarioNavigationTests.LogicalDescendants(dialog).OfType<FrameworkElement>().ToArray();
                    var texts = string.Join("\n", controls.OfType<TextBlock>().Select(t => t.Text));
                    Assert.IsTrue(texts.Contains("3/10") && texts.Contains("6/10") && texts.Contains("9/10"));
                    var cards = controls.OfType<Button>().Where(b => AutomationProperties.GetAutomationId(b).StartsWith("Music.Poetry.Cloud.Choose.")).ToArray();
                    Assert.AreEqual(2, cards.Length);
                    Assert.IsNotNull(cards[0].Style, "Chooser card must resolve the themed action style.");
                    var recommended = cards.Single(b => b.BorderThickness.Left == 2);
                    Assert.IsTrue(AutomationProperties.GetAutomationId(recommended).EndsWith(language == "en" ? "deepseek" : "alice"));
                    if (Environment.GetEnvironmentVariable("LOPATA_CLOUD_UI_PROOF") is { Length: > 0 } proof)
                    {
                        Directory.CreateDirectory(proof);
                        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(dialog.ActualWidth), (int)Math.Ceiling(dialog.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                        bitmap.Render(dialog); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var output = File.Create(Path.Combine(proof, language + (dark ? "-dark" : "-light") + ".png")); encoder.Save(output);
                    }
                    cards.Single(b => AutomationProperties.GetAutomationId(b).EndsWith("deepseek")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                catch (Exception ex) { failure = ex; dialog.Close(); }
            }), DispatcherPriority.ApplicationIdle);
            Assert.AreEqual("deepseek", Call(editor, "ChooseCloudProvider"));
            if (failure is not null) throw failure;
            Assert.IsNull(preferences.Load().CloudAssistantId);
            Assert.IsFalse(Get<bool>(editor, "_searchEnabled"));
            var button = Get<Button>(editor, "_search");
            button.ContextMenu!.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            var defaults = (MenuItem)button.ContextMenu.Items[0];
            ((MenuItem)defaults.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.AreEqual("deepseek", preferences.Load().CloudAssistantId);
            Assert.IsFalse(Get<bool>(editor, "_searchEnabled"));
            ((MenuItem)defaults.Items[2]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.IsNull(preferences.Load().CloudAssistantId);
        }
        finally { editor.Close(); }
    });

    private static object? Call(object target, string method) => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null);
    private static T Get<T>(object target, string field) => (T)target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
}
