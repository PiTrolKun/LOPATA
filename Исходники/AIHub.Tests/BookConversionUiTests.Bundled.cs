using System.Windows;
using AIHub.Controls;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace AIHub.Tests;

public sealed partial class BookConversionUiTests
{
    [TestMethod, DataRow("ru", true), DataRow("en", false)]
    public Task BundledComponentsPanelIsAbsent(string language, bool dark) => Sta(async () =>
    {
        var owner = new Window(); Theme(owner, dark); owner.Show();
        var localizer = new LocalizationService(); localizer.Load(language);
        var window = new BookConversionWindow(owner, localizer.T);
        try
        {
            window.Show(); await Render(window);
            Click(Find<Button>(window, "Simple")); await Render(window);
            Assert.IsFalse(VisualDesc<Button>(window).Any(button =>
                System.Windows.Automation.AutomationProperties.GetAutomationId(button) is "BookConverter.Prepare" or "BookConverter.PrepareOffice"));
            Assert.IsFalse(VisualDesc<TextBlock>(window).Any(text => text.Text == localizer.T("BookConverter.ComponentsHint")
                || text.Text == localizer.T("BookConverter.Components")));
        }
        finally { window.Close(); owner.Close(); }
    });
}
