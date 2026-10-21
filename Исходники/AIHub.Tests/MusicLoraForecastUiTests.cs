using System.Reflection;
using System.Windows.Controls;
using System.Windows.Media;
using AIHub.Controls;
using AIHub.Services;
using AIHub.Services.MusicLora;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicLoraForecastUiTests
{
    [TestMethod]
    [DataRow(false, true, .65, 2_000_000_000L, "NoAdditional", false)]
    [DataRow(true, false, .65, 2_000_000_000L, "NoAdditional", false)]
    [DataRow(true, true, 0d, 2_000_000_000L, "NoAdditional", false)]
    [DataRow(true, true, .65, 2_000_000_000L, "BaselineLow", true)]
    [DataRow(true, true, .65, 900_000_000L, "Low", true)]
    [DataRow(true, true, .65, 100_000_000L, "Fits", false)]
    public Task ForecastDistinguishesBypassFromAlreadyInsufficientBaseline(bool masterOn, bool selected,
        double strength, long baseline, string expected, bool warning) => ScenarioNavigationTests.Sta(() => {
        using var files = new MusicProjectTests.Files(); var localizer = new LocalizationService(); localizer.Load("ru");
        var selection = new MusicLoraSelection { ByModel = new() {
            [MusicAceCatalog.Variation] = [new("jay2", strength) { Enabled = selected }] } };
        var window = new MusicLoraWindow(null, files.Root, MusicAceCatalog.Variation, selection, "pop", "words",
            MusicAceCatalog.Defaults(), 8, localizer.T);
        try {
            // Hardware observations are supplied only to this isolated view; no real probe or global cache is changed.
            Field("_free").SetValue(window, 1_000_000_000L); Field("_baseline").SetValue(window, baseline);
            var master = (System.Windows.Controls.CheckBox)Field("_master").GetValue(window)!;
            master.IsChecked = !masterOn; master.IsChecked = masterOn;
            var forecast = (TextBlock)Field("_forecast").GetValue(window)!;
            Assert.Contains(localizer.T("Music.Lora." + expected), forecast.Text);
            Assert.AreEqual(warning, Equals(forecast.Foreground, Brushes.OrangeRed));
            Assert.AreEqual(strength, window.Selection.ByModel[MusicAceCatalog.Variation].Single().Strength);
            Assert.AreEqual(selected, window.Selection.ByModel[MusicAceCatalog.Variation].Single().Enabled);
        } finally { window.Close(); }
        static FieldInfo Field(string name) => typeof(MusicLoraWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    });
}
