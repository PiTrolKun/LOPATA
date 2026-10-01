using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Services;
using static AIHub.Tests.ScenarioNavigationTests;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class ScenarioTagCloudTests
{
    [TestMethod]
    public void TagsResolveOnlyToAvailableEntrancesAndHaveBothTranslations()
    {
        var tags = ScenarioNavigationCatalog.CloudTags;
        Assert.AreEqual(tags.Count, tags.Select(t => t.Id).Distinct().Count());
        foreach (var language in new[] { "ru", "en" })
        {
            var localizer = new LocalizationService(); localizer.Load(language);
            foreach (var tag in tags)
            {
                var target = ScenarioNavigationCatalog.Get(tag.TargetId);
                Assert.IsTrue(target.IsAvailable);
                Assert.IsTrue(target.Kind is ScenarioNavigationKind.Direction or
                    ScenarioNavigationKind.Group or ScenarioNavigationKind.Scenario);
                Assert.AreNotEqual(tag.TitleKey, localizer.T(tag.TitleKey));
                Assert.AreNotEqual(tag.DescriptionKey, localizer.T(tag.DescriptionKey));
            }
            foreach (var key in new[] { "Hint", "OpenDescription", "Pause", "Resume", "Go", "CloseDescription", "Destination" })
                Assert.AreNotEqual("Cloud." + key, localizer.T("Cloud." + key));
        }
    }

    [TestMethod]
    public void SpherePositionsStayFiniteAndDepthPreservesPerspective()
    {
        Assert.AreEqual(0, ScenarioTagSphere.CreatePoints(0).Count);
        foreach (var point in ScenarioTagSphere.CreatePoints(80))
        {
            Assert.AreEqual(1d, point.X * point.X + point.Y * point.Y + point.Z * point.Z, 1e-10);
            foreach (var yaw in new[] { 0d, 1d, 10000d })
            {
                var p = ScenarioTagSphere.Project(point, yaw, 1.4);
                Assert.IsTrue(double.IsFinite(p.X) && double.IsFinite(p.Y));
                Assert.IsTrue(p.Depth is >= -1 and <= 1);
                Assert.IsTrue(p.Opacity is >= .36 and <= 1);
            }
        }
        var front = ScenarioTagSphere.Project(new(0, 0, 1), 0, 0);
        var rear = ScenarioTagSphere.Project(new(0, 0, -1), 0, 0);
        Assert.IsTrue(front.Scale > rear.Scale && front.Opacity > rear.Opacity);
    }

    [TestMethod]
    public void DragAndCaptureCancellationNeverOpenDescription()
    {
        var gesture = new CloudPointerGesture();
        gesture.Begin(10, 20, "advisor");
        Assert.AreEqual("advisor", gesture.End(12, 21, "advisor"));
        gesture.Begin(10, 20, "advisor"); gesture.Move(40, 20); gesture.Move(10, 20);
        Assert.IsNull(gesture.End(10, 20, "advisor"));
        gesture.Begin(10, 20, "advisor"); Assert.IsNull(gesture.End(10, 20, "writer"));
        gesture.Begin(10, 20, "advisor"); gesture.Cancel(); Assert.IsNull(gesture.End(10, 20, "advisor"));
        gesture.Begin(0, 0, null); Assert.IsNull(gesture.End(0, 0, null));
    }

    [TestMethod]
    [DataRow("ru", 1300d, 1.25d, true, 1, 0)]
    [DataRow("en", 1300d, 1.25d, false, 1, 0)]
    [DataRow("ru", 650d, 1.5d, true, 0, 1)]
    public Task CommonCloudAppearsOnlyBesideCompactDirectionCards(string language, double width,
        double scale, bool dark, int column, int row) => Sta(() =>
    {
        var nav = Create(language, scale, dark);
        Assert.IsFalse(LogicalDescendants(nav).Contains(nav.TagCloud));
        foreach (var id in new[] { ScenarioNavigationCatalog.Creation, ScenarioNavigationCatalog.Analysis,
            ScenarioNavigationCatalog.Experiments })
        {
            nav.SelectDirection(id);
            nav.Measure(new Size(width, 850)); nav.Arrange(new Rect(0, 0, width, 850)); nav.UpdateLayout();
            Assert.IsTrue(LogicalDescendants(nav).Contains(nav.TagCloud));
            Assert.AreEqual(column, Grid.GetColumn(nav.TagCloud)); Assert.AreEqual(row, Grid.GetRow(nav.TagCloud));
            var split = (Grid)nav.TagCloud.Parent;
            var list = split.Children.OfType<ScrollViewer>().Single();
            Assert.AreEqual(0, Grid.GetRow(list));
            if (row == 1)
                Assert.IsTrue(nav.TagCloud.TranslatePoint(new Point(), split).Y >= list.ActualHeight - 1);
            var buttons = LogicalDescendants(nav).OfType<Button>().ToArray();
            Assert.AreEqual(ScenarioNavigationCatalog.CloudTags.Count,
                buttons.Count(b => AutomationProperties.GetAutomationId(b).StartsWith("Cloud.Tag.")));
            var card = buttons.Single(b => b.Tag is string tag && ScenarioNavigationCatalog.Nodes.Any(n =>
                n.Id == tag && n.Kind == ScenarioNavigationKind.Scenario));
            Assert.IsTrue(card.ActualHeight < 220 * scale);
            Assert.IsTrue(card.Content is Grid);
            foreach (var text in LogicalDescendants(card).OfType<TextBlock>())
                Assert.IsTrue(text.ActualWidth <= card.ActualWidth);
        }
        nav.OpenScenario(ScenarioNavigationCatalog.Sandbox);
        Assert.IsFalse(LogicalDescendants(nav).Contains(nav.TagCloud));
        nav.ShowHome(); Assert.IsFalse(LogicalDescendants(nav).Contains(nav.TagCloud));
    });

    [TestMethod]
    public Task CloudUsesCanonicalTargetAndReturnsToItsSourceWithoutStartingSandbox() => Sta(() =>
    {
        var nav = Create("ru", 1, true);
        var requests = new List<string>(); nav.ScenarioRequested += requests.Add;
        nav.SelectDirection(ScenarioNavigationCatalog.Analysis);
        // An incoming target is ignored in favor of the shared catalog's target.
        var advisor = ScenarioNavigationCatalog.GetTag("advisor") with { TargetId = "uncertainty" };
        nav.NavigateFromTag(advisor);
        CollectionAssert.AreEqual(new[] { ScenarioNavigationCatalog.Literary }, requests);
        Assert.IsTrue(nav.ReturnFromScenario());
        Assert.AreEqual(ScenarioNavigationCatalog.Analysis, nav.SelectedDirectionId);
        Assert.IsFalse(nav.ReturnFromScenario());
        nav.NavigateFromTag(ScenarioNavigationCatalog.GetTag("research"));
        Assert.IsTrue(nav.IsSandboxLanding); Assert.AreEqual(1, requests.Count);
        nav.GoBack(); Assert.AreEqual(ScenarioNavigationCatalog.Analysis, nav.SelectedDirectionId);
        nav.NavigateFromTag(ScenarioNavigationCatalog.GetTag("literature"));
        Assert.AreEqual(ScenarioNavigationCatalog.Creation, nav.SelectedDirectionId);
        nav.GoBack(); Assert.AreEqual(ScenarioNavigationCatalog.Analysis, nav.SelectedDirectionId);
        nav.NavigateFromTag(ScenarioNavigationCatalog.GetTag("images"));
        Assert.IsFalse(nav.ReturnFromScenario()); // Same folder does not introduce an extra back step.
    });

    [TestMethod]
    public Task RenderingSubscriptionStopsForHiddenMinimizedPausedAndUnloadedWindow() => Sta(() =>
    {
        var nav = Create("ru", 1, true); nav.SelectDirection(ScenarioNavigationCatalog.Creation);
        var window = new Window { Content = nav, Width = 1100, Height = 750, Left = -2500,
            ShowInTaskbar = false, ShowActivated = false };
        try
        {
            window.Show(); Pump();
            Assert.AreEqual(SystemParameters.ClientAreaAnimation, nav.TagCloud.IsRenderingActive);
            var tagButton = LogicalDescendants(nav.TagCloud).OfType<Button>().First();
            tagButton.Focus();
            tagButton.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(tagButton), 0, System.Windows.Input.Key.Tab)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            AssertStopped(nav.TagCloud);
            tagButton.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,
                0, System.Windows.Input.MouseButton.Right) { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseDownEvent });
            Assert.AreEqual(SystemParameters.ClientAreaAnimation, nav.TagCloud.IsRenderingActive);
            nav.TagCloud.SetManualPause(true); AssertStopped(nav.TagCloud);
            nav.TagCloud.SetManualPause(false);
            nav.TagCloud.SuspendForDescription(true); AssertStopped(nav.TagCloud);
            nav.TagCloud.SuspendForDescription(false);
            window.WindowState = WindowState.Minimized; Pump(); AssertStopped(nav.TagCloud);
            window.WindowState = WindowState.Normal; Pump();
            window.Hide(); Pump(); AssertStopped(nav.TagCloud);
            window.Show(); Pump();
            nav.ShowHome(); Pump(); AssertStopped(nav.TagCloud);
        }
        finally { window.Close(); Pump(); }
    });

    private static void AssertStopped(ScenarioTagCloudControl cloud)
    {
        Assert.IsFalse(cloud.IsRenderingActive);
        var frames = cloud.RenderedFrameCount; Pump(); Assert.AreEqual(frames, cloud.RenderedFrameCount);
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
}
