using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ComboBox = System.Windows.Controls.ComboBox;

namespace AIHub.Tests;

[TestClass]
public sealed class FinancialCurrencyTests
{
    [TestMethod]
    [DataRow("ru", "RUB")]
    [DataRow("en", "USD")]
    public void FullOfflineCatalogAndLanguageDefaultReachEveryAnalystAndReport(string language, string expected)
    {
        Assert.AreEqual(176, FinancialCurrencies.All.Count);
        Assert.AreEqual(176, FinancialCurrencies.All.Select(c => c.Code).Distinct().Count());
        Assert.IsNull(FinancialCurrencies.Find("XXX")); Assert.IsNull(FinancialCurrencies.Find("XTS"));
        Assert.IsNotNull(FinancialCurrencies.Find("AED")); Assert.IsNotNull(FinancialCurrencies.Find("JPY"));
        var input = FinancialScenarioTests.CompleteInput() with { Language = language, Unit = "" };
        var calculation = FinancialCalculator.Calculate(input);
        var localizer = new LocalizationService(); localizer.Load(language);
        StringAssert.Contains(FinancialReportExporter.CalculationText(input, calculation, localizer.T), expected);
        foreach (var stage in FinancialAnalysisPlan.Stages)
        {
            var prompt = FinancialAnalysisPipeline.BuildPrompt(stage, input, calculation, []);
            var data = prompt.Split("DATA:\n")[1].Split(FinancialAnalysisPrompts.DataEnd)[0];
            using var json = JsonDocument.Parse(data);
            Assert.AreEqual(expected, json.RootElement.GetProperty("Unit").GetString());
        }
        var changed = input with { Unit = "JPY" };
        Assert.AreEqual(JsonSerializer.Serialize(calculation), JsonSerializer.Serialize(FinancialCalculator.Calculate(changed)));
        StringAssert.Contains(FinancialReportExporter.CalculationText(changed, calculation, localizer.T), "JPY");
        Assert.AreEqual("условные единицы", FinancialCurrencies.Resolve("условные единицы", language));
    }

    [TestMethod]
    public async Task SetupSelectionSurvivesBackAndLanguageChanges() => await ScenarioNavigationTests.Sta(() =>
    {
        var control = new FinancialScenarioControl();
        var localizer = new LocalizationService(); localizer.Load("en");
        var settings = new StorageSettings(); var profile = new UserProfile();
        var context = new UserContextService(new(), new());
        void Configure(string language) { localizer.Load(language); control.Configure(localizer.T, language, settings, profile, context, []); }
        T Find<T>(string id) where T : System.Windows.DependencyObject => ScenarioNavigationTests.LogicalDescendants(control)
            .OfType<T>().Single(e => AutomationProperties.GetAutomationId(e) == "Finance." + id);
        string Selected() => (string)Find<ComboBox>("Currency").SelectedItem.GetType().GetProperty("Id")!.GetValue(Find<ComboBox>("Currency").SelectedItem)!;
        Configure("en"); Assert.AreEqual("USD", Selected());
        localizer.Load("ru"); control.Localize(localizer.T, "ru"); Assert.AreEqual("RUB", Selected());
        var currencies = Find<ComboBox>("Currency"); Assert.AreEqual(176, currencies.Items.Count);
        currencies.SelectedItem = currencies.Items.Cast<object>().Single(c => (string)c.GetType().GetProperty("Id")!.GetValue(c)! == "JPY");
        Find<System.Windows.Controls.Button>("Begin").RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Assert.IsTrue(control.GoBack()); Assert.AreEqual("JPY", Selected());
        localizer.Load("en"); control.Localize(localizer.T, "en"); Assert.AreEqual("JPY", Selected());
    });

    [TestMethod]
    [DataRow("¹⁶⁰⁰⁰")]
    [DataRow("½")]
    [DataRow("２０００")]
    public void UnicodeNumbersCannotBypassQualitativeReplyCheck(string number)
    {
        Assert.IsTrue(FinancialReplyQuality.Issues("Неподтверждённая сумма " + number, "ru", "RUB").Count > 0);
        Assert.AreEqual(0, FinancialReplyQuality.Issues("Статья выражена в рублях.", "ru", "RUB").Count);
        Assert.IsTrue(FinancialReplyQuality.Issues("Статья выражена в рублях.", "ru", "USD").Count > 0);
        Assert.AreEqual(0, FinancialReplyQuality.Issues("The data uses US dollars.", "en", "USD").Count);
        Assert.IsTrue(FinancialReplyQuality.Issues("The data uses EUR.", "en", "USD").Count > 0);
    }
}
