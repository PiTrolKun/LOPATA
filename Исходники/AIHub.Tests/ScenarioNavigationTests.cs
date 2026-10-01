using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using AIHub.Controls;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class ScenarioNavigationTests
{
    [TestMethod]
    public void CatalogKeepsCanonicalEntriesAndResolvableLinks()
    {
        var nodes = ScenarioNavigationCatalog.Nodes;
        Assert.AreEqual(nodes.Count, nodes.Select(n => n.Id).Distinct().Count());
        Assert.AreEqual(4, ScenarioNavigationCatalog.Children(null).Count());
        Assert.IsFalse(ScenarioNavigationCatalog.Get(ScenarioNavigationCatalog.Utilities).IsAvailable);
        Assert.AreEqual("uncertainty", ScenarioNavigationCatalog.Get(ScenarioNavigationCatalog.Sandbox).Id);
        foreach (var node in nodes)
        {
            Assert.IsNotNull(ScenarioNavigationCatalog.Get(node.EntryTargetId));
            if (node.ParentId is not null) Assert.IsNotNull(ScenarioNavigationCatalog.Get(node.ParentId));
            foreach (var related in node.RelatedIds) Assert.IsNotNull(ScenarioNavigationCatalog.Get(related));
            foreach (var language in new[] { "ru", "en" })
            {
                var localizer = new LocalizationService(); localizer.Load(language);
                Assert.AreNotEqual(node.TitleKey, localizer.T(node.TitleKey));
                if (node.DescriptionKey.Length != 0) Assert.AreNotEqual(node.DescriptionKey, localizer.T(node.DescriptionKey));
            }
        }
    }

    [TestMethod]
    public Task SandboxLandingDoesNotStartTaskAndRetainsHistoryAcrossNavigation() => Sta(() =>
    {
        var control = Create("ru", 1, true);
        var title = new TextBox { Text = "Saved title" };
        var history = new Expander { IsExpanded = true, Content = title };
        control.AttachSandboxHistory(history);
        var requests = new List<string>(); control.ScenarioRequested += requests.Add;
        Assert.IsFalse(LogicalDescendants(control).Contains(history));
        control.SelectDirection(ScenarioNavigationCatalog.Experiments);
        control.OpenScenario(ScenarioNavigationCatalog.Sandbox);
        Assert.AreEqual(0, requests.Count);
        Assert.IsTrue(control.IsSandboxLanding);
        Assert.IsTrue(LogicalDescendants(control).Contains(history));
        control.Configure(key => key); // A language refresh must not replace the history or reset the page.
        Assert.IsTrue(history.IsExpanded);
        Assert.AreEqual("Saved title", title.Text);
        var start = LogicalDescendants(control).OfType<Button>().Single(b =>
            System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "Navigation.SandboxNew");
        start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        CollectionAssert.AreEqual(new[] { "uncertainty" }, requests);
        Assert.IsTrue(control.GoBack());
        Assert.IsFalse(LogicalDescendants(control).Contains(history));
        Assert.AreEqual(ScenarioNavigationCatalog.Experiments, control.SelectedDirectionId);
        Assert.IsTrue(control.GoBack()); Assert.IsTrue(control.IsHome);
        Assert.IsFalse(control.GoBack());
        control.SelectDirection(ScenarioNavigationCatalog.Utilities); Assert.IsTrue(control.IsHome);
        control.SelectDirection(ScenarioNavigationCatalog.Creation);
        control.OpenScenario(ScenarioNavigationCatalog.Literary);
        Assert.AreEqual(ScenarioNavigationCatalog.Literary, requests[^1]);
        Assert.AreEqual(ScenarioNavigationCatalog.Creation, control.SelectedDirectionId);
    });

    [TestMethod]
    [DataRow("ru", 1200d, 1d, true, 2)]
    [DataRow("en", 1200d, 1.5d, false, 2)]
    [DataRow("ru", 500d, 1.5d, true, 1)]
    [DataRow("en", 500d, 1.25d, false, 1)]
    public Task TilesWrapAndAdaptWithoutClipping(string language, double width, double scale, bool dark, int columns) => Sta(() =>
    {
        var control = Create(language, scale, dark);
        control.Measure(new Size(width, 750)); control.Arrange(new Rect(0, 0, width, 750)); control.UpdateLayout();
        var tiles = LogicalDescendants(control).OfType<UniformGrid>().Single();
        Assert.AreEqual(columns, tiles.Columns);
        Assert.AreEqual(4, tiles.Children.Count);
        foreach (Button tile in tiles.Children)
        {
            Assert.IsTrue(tile.ActualWidth > 0);
            var content = (StackPanel)tile.Content;
            Assert.IsTrue(content.ActualWidth <= tile.ActualWidth);
            foreach (var text in content.Children.OfType<TextBlock>())
            {
                Assert.AreEqual(TextWrapping.Wrap, text.TextWrapping);
                Assert.IsTrue(text.ActualWidth <= content.ActualWidth + 1);
            }
        }
    });

    internal static ScenarioNavigationControl Create(string language, double scale, bool dark)
    {
        var control = new ScenarioNavigationControl();
        foreach (var (key, size) in new[] { ("UiBodyFontSize", 14d), ("UiSmallFontSize", 12d),
            ("UiCardTitleFontSize", 24d), ("UiSectionFontSize", 18d), ("UiPageTitleFontSize", 32d) })
            control.Resources[key] = size * scale;
        control.Resources["TextPrimaryBrush"] = new SolidColorBrush(dark ? Colors.White : Colors.Black);
        control.Resources["TextSecondaryBrush"] = Brushes.Gray;
        control.Resources["PanelBrush"] = new SolidColorBrush(dark ? Color.FromRgb(23, 32, 50) : Colors.White);
        control.Resources["LineBrush"] = Brushes.Gray;
        control.Resources["AccentBrush"] = Brushes.RoyalBlue;
        var localizer = new LocalizationService(); localizer.Load(language); control.Configure(localizer.T);
        return control;
    }

    internal static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in LogicalDescendants(child)) yield return descendant;
    }

    internal static async Task Sta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception error) { completion.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
