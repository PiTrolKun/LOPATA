using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class ComponentAuditUiTests
{
    [TestMethod, DataRow("ru", true), DataRow("en", true), DataRow("ru", false), DataRow("en", false)]
    public Task IndicatorAndRecoveryActionAreLocalizedAndPreserveInput(string language, bool dark) => ScenarioNavigationTests.Sta(() => {
        var localize = new LocalizationService(); localize.Load(language);
        var indicator = new ComponentAuditIndicator { Width = 28, Height = 24 };
        indicator.Localize(localize.T);
        var input = new TextBox { Text = "A project draft that must stay unchanged" };
        var panel = new StackPanel(); panel.Children.Add(input); panel.Children.Add(indicator);
        var host = new Window { Content = panel, Width = 580, Height = 250, ShowInTaskbar = false };
        var theme = ScenarioNavigationTests.Create(language, 1, dark).Resources;
        foreach (var key in theme.Keys) host.Resources[key] = theme[key];
        host.Resources["SecondaryButtonStyle"] = new Style(typeof(Button));
        try {
            host.Show(); host.UpdateLayout();
            indicator.ApplyState(true);
            Assert.AreEqual(Color.FromRgb(192, 39, 48), ((SolidColorBrush)indicator.Background).Color);
            Assert.AreEqual(localize.T("ComponentAudit.Enabled"), AutomationProperties.GetName(indicator));
            Assert.AreEqual("ComponentAudit.Toggle", AutomationProperties.GetAutomationId(indicator));
            indicator.ShowStartupNotice();
            Assert.IsTrue(indicator.IsNoticeOpen);
            Assert.AreEqual(localize.T("ComponentAudit.StartupMessage"), indicator.NoticeMessage);
            indicator.ApplyState(false);
            Assert.IsFalse(indicator.IsNoticeOpen);
            Assert.AreEqual(Color.FromRgb(17, 121, 66), ((SolidColorBrush)indicator.Background).Color);
            Assert.AreEqual(localize.T("ComponentAudit.Disabled"), AutomationProperties.GetName(indicator));
            indicator.ShowFailureNotice();
            Assert.IsTrue(indicator.IsNoticeOpen);
            Assert.AreEqual(Visibility.Visible, indicator.EnableAction.Visibility);
            Assert.AreEqual(localize.T("ComponentAudit.Enable"), indicator.EnableAction.Content);
            if (Environment.GetEnvironmentVariable("LOPATA_COMPONENT_AUDIT_UI_PROOF") is { Length: > 0 } output) {
                Directory.CreateDirectory(output);
                var notice = indicator.NoticeView;
                notice.Measure(new Size(450, double.PositiveInfinity));
                notice.Arrange(new Rect(new System.Windows.Point(), notice.DesiredSize)); notice.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(notice.ActualWidth), (int)Math.Ceiling(notice.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(notice); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var image = File.Create(Path.Combine(output, $"{language}-{dark}-recovery.png")); encoder.Save(image);
            }
            var enabled = 0;
            indicator.EnableRequested += () => { enabled++; indicator.ApplyState(true); };
            indicator.EnableAction.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.AreEqual(1, enabled);
            Assert.IsFalse(indicator.IsNoticeOpen);
            Assert.AreEqual("A project draft that must stay unchanged", input.Text);
            indicator.ShowFailureNotice();
            Assert.IsFalse(indicator.IsNoticeOpen, "An enabled audit must not prompt to enable itself.");
        }
        finally { indicator.HideNotice(); host.Close(); }
    });

    [TestMethod]
    public void FirstDisableStopsRemindersAfterReenableAndRoundTrip()
    {
        var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"behavior\":{}}", json)!;
        Assert.IsTrue(settings.Behavior.ComponentAuditEnabled);
        Assert.IsFalse(settings.Behavior.ComponentAuditEverDisabled);
        Assert.IsTrue(MainWindow.ShouldRemindComponentAudit(settings.Behavior));
        MainWindow.ApplyComponentAuditChoice(settings.Behavior, false);
        Assert.IsFalse(settings.Behavior.ComponentAuditEnabled);
        Assert.IsTrue(settings.Behavior.ComponentAuditEverDisabled);
        MainWindow.ApplyComponentAuditChoice(settings.Behavior, true);
        settings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, json), json)!;
        Assert.IsTrue(settings.Behavior.ComponentAuditEnabled);
        Assert.IsTrue(settings.Behavior.ComponentAuditEverDisabled);
        Assert.IsFalse(MainWindow.ShouldRemindComponentAudit(settings.Behavior));
    }

    [TestMethod]
    public Task HiddenWindowDefersNoticeWithoutOpeningItself() => ScenarioNavigationTests.Sta(() => {
        var indicator = new ComponentAuditIndicator();
        var host = new Window { Content = indicator, Width = 300, Height = 180, ShowInTaskbar = false };
        try {
            indicator.ShowStartupNotice();
            Assert.IsFalse(host.IsVisible);
            Assert.IsFalse(indicator.IsNoticeOpen);
            host.Show(); host.UpdateLayout();
            Assert.IsTrue(indicator.IsNoticeOpen);
            host.Hide();
            Assert.IsFalse(indicator.IsNoticeOpen);
            host.Show(); host.UpdateLayout();
            Assert.IsTrue(indicator.IsNoticeOpen, "A hidden window must retain the unread notice for its return.");
        }
        finally { indicator.HideNotice(); host.Close(); }
    });
}
