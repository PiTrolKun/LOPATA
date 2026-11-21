using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls.ImageLayers;
using AIHub.Services;
using AIHub.Services.ImageLayers;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using MenuItem = System.Windows.Controls.MenuItem;
using PixelRect = System.Drawing.Rectangle;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class ImageLayerMenuUiTests
{
    [TestMethod]
    public Task DoubleClickOnNestedLayerAnimatesToItsBounds() => ScenarioNavigationTests.Sta(() =>
    {
        var owner = new Window { Width = 1060, Height = 720, ShowInTaskbar = false, ShowActivated = false };
        var scenario = new ImageLayersControl(owner, key => key, () => true, null!, () => null!);
        owner.Content = scenario;
        const int width = 640;
        const int height = 400;
        var document = new LayerDocument(new LayerSource("focus.png", [], width, height, new byte[width * height * 4]));
        var childBounds = new PixelRect(602, 20, 5, 13);
        var proposal = new LayerProposal([
            new("group", null, "Group", LayerMask.Area(width, height, new(75, 150, 40, 30))),
            new("child", "group", "Child", LayerMask.Area(width, height, childBounds))
        ], []);
        document.Commit(document.PlanAnalysis(proposal, document.Source.Frame, null), LayerDocument.AnalyzeKind);
        var groupId = document.Layers.Values.Single(layer => layer.Parent is null).Id;
        typeof(ImageLayersControl).GetMethod("SetDocument", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(scenario, [document, null, groupId]);
        var canvas = ScenarioNavigationTests.LogicalDescendants(scenario).OfType<LayerCanvas>().Single();
        var tree = ScenarioNavigationTests.LogicalDescendants(scenario).OfType<TreeView>().Single();
        var group = (TreeViewItem)tree.Items[0];
        var child = (TreeViewItem)group.Items[0];
        try
        {
            owner.Show();
            owner.UpdateLayout();
            var childHeader = VisualDescendants(child).OfType<ContentPresenter>().Single(header => Equals(header.Content, "Child"));
            var expander = VisualDescendants(group).OfType<ToggleButton>().First();
            var initial = ScreenBounds(childBounds);
            var frames = new List<Rect>();
            canvas.ZoomChanged += _ => frames.Add(ScreenBounds(childBounds));

            Assert.IsTrue(DoubleClick(childHeader));
            Assert.AreSame(child, tree.SelectedItem);
            Assert.IsTrue(group.IsExpanded);
            Assert.AreEqual(initial, ScreenBounds(childBounds), "The first frame starts at the current view.");

            var scale = Math.Min((canvas.ActualWidth - 32) / childBounds.Width, (canvas.ActualHeight - 32) / childBounds.Height);
            var fittedWidth = childBounds.Width * scale;
            PumpUntil(() => Math.Abs(ScreenBounds(childBounds).Width - fittedWidth) < .001);
            Assert.IsTrue(frames.Any(bounds => bounds.Width > initial.Width && bounds.Width < fittedWidth - .01));
            var fitted = ScreenBounds(childBounds);
            Assert.AreEqual(canvas.ActualWidth / 2, fitted.Left + fitted.Width / 2, .001);
            Assert.AreEqual(canvas.ActualHeight / 2, fitted.Top + fitted.Height / 2, .001);
            Assert.IsTrue(fitted.Left >= 15.99 && fitted.Top >= 15.99);
            Assert.IsTrue(scale > 10, "A very small layer can use the whole canvas.");
            canvas.Zoom(1 / 1.2);
            Assert.AreEqual(fittedWidth / 1.2, ScreenBounds(childBounds).Width, .001);
            canvas.Fit();
            Assert.AreEqual(initial, ScreenBounds(childBounds));

            DoubleClick(expander);
            var idle = Stopwatch.StartNew();
            PumpUntil(() => idle.ElapsedMilliseconds > 300);
            Assert.AreEqual(initial, ScreenBounds(childBounds), "The expander does not focus a layer.");

            group.IsExpanded = true;
            owner.UpdateLayout();
            childHeader = VisualDescendants(child).OfType<ContentPresenter>().Single(header => Equals(header.Content, "Child"));
            Assert.IsTrue(DoubleClick(childHeader));
            canvas.Zoom(1.2);
            var manual = ScreenBounds(childBounds);
            idle.Restart();
            PumpUntil(() => idle.ElapsedMilliseconds > 300);
            Assert.AreEqual(manual, ScreenBounds(childBounds), "Manual zoom stops the focus animation.");
        }
        finally { owner.Close(); }

        Rect ScreenBounds(PixelRect bounds) => (Rect)typeof(LayerCanvas)
            .GetMethod("ScreenRect", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(canvas, [bounds])!;
    });

    private static bool DoubleClick(UIElement hit)
    {
        var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = Mouse.PreviewMouseDownEvent };
        typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!.SetValue(click, 2);
        hit.RaiseEvent(click);
        return click.Handled;
    }

    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in VisualDescendants(child)) yield return descendant;
        }
    }

    private static void PumpUntil(Func<bool> ready)
    {
        var frame = new DispatcherFrame();
        var clock = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (ready() || clock.ElapsedMilliseconds > 2500) frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Assert.IsTrue(ready(), "The viewport did not finish updating.");
    }

    [TestMethod]
    public Task OpeningWithAnEmptyLayerSelectsAVisibleOneAndKeepsViewAvailable() => ScenarioNavigationTests.Sta(() =>
    {
        var owner = new Window();
        var localizer = new LocalizationService();
        localizer.Load("ru");
        var scenario = new ImageLayersControl(owner, localizer.T, () => true, null!, () => null!);
        owner.Content = scenario;
        var document = new LayerDocument(new LayerSource("text.png", [], 4, 4, new byte[64]));
        var visibleShape = LayerMask.Area(4, 4, new(0, 0, 2, 2));
        var old = document.PlanAdditionalLayer("Надпись", visibleShape);
        var oldId = old.Keys.Single();
        document.Commit(old, LayerDocument.AnalyzeKind);
        document.Commit(document.PlanAdditionalLayer("Строка", visibleShape), LayerDocument.AnalyzeKind);
        typeof(ImageLayersControl).GetMethod("SetDocument", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(scenario, [document, null, oldId]);
        var view = ScenarioNavigationTests.LogicalDescendants(scenario).OfType<System.Windows.Controls.ComboBox>()
            .Single(control => AutomationProperties.GetAutomationId(control) == "ImageLayers.View");
        var tree = ScenarioNavigationTests.LogicalDescendants(scenario).OfType<TreeView>()
            .Single(control => AutomationProperties.GetAutomationId(control) == "ImageLayers.Tree");
        Assert.AreEqual("Строка", ((TreeViewItem)tree.SelectedItem).Header);
        Assert.IsTrue(view.IsEnabled);
        view.SelectedIndex = 1;
        Assert.AreEqual(1, view.SelectedIndex);
        ((TreeViewItem)tree.Items[1]).IsSelected = true;
        Assert.IsTrue(view.IsEnabled);
        Assert.AreEqual(0, view.SelectedIndex);
        Assert.IsFalse(((ComboBoxItem)view.Items[1]).IsEnabled);
        Assert.AreEqual("Надпись (без обводки)", ((TreeViewItem)tree.SelectedItem).Header);
    });

    [TestMethod]
    public Task AdditionalToolsMenuHasNoSystemGutterInEitherTheme() => ScenarioNavigationTests.Sta(() =>
    {
        var owner = new Window { Width = 1060, Height = 620, ShowInTaskbar = false, ShowActivated = false };
        var localizer = new LocalizationService();
        localizer.Load("ru");
        var scenario = new ImageLayersControl(owner, localizer.T, () => true, null!, () => null!);
        owner.Content = scenario;
        var document = new LayerDocument(new LayerSource("theme.png", [], 2, 2, new byte[16]));
        var layer = document.PlanAdditionalLayer("Объект", LayerMask.Area(2, 2, new(0, 0, 1, 2)));
        document.Commit(layer, LayerDocument.AnalyzeKind);
        typeof(ImageLayersControl).GetMethod("SetDocument", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(scenario, [document, null, layer.Keys.Single()]);
        var button = ScenarioNavigationTests.LogicalDescendants(scenario).OfType<Button>()
            .Single(control => AutomationProperties.GetAutomationId(control) == "ImageLayers.AdditionalTools");
        var menu = button.ContextMenu!;
        try
        {
            owner.Show();
            foreach (var dark in new[] { true, false })
            {
                var panel = dark ? Color.FromRgb(23, 32, 51) : Colors.White;
                var text = dark ? Color.FromRgb(248, 250, 252) : Color.FromRgb(31, 31, 31);
                var line = dark ? Color.FromRgb(45, 55, 75) : Color.FromRgb(218, 221, 227);
                owner.Resources["PanelBrush"] = new SolidColorBrush(panel);
                owner.Resources["WindowBackgroundBrush"] = new SolidColorBrush(panel);
                owner.Resources["TextPrimaryBrush"] = new SolidColorBrush(text);
                owner.Resources["LineBrush"] = new SolidColorBrush(line);
                owner.UpdateLayout();

                Assert.IsTrue(button.IsEnabled);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                menu.UpdateLayout();
                Assert.IsTrue(menu.IsOpen);
                var items = menu.Items.OfType<MenuItem>().ToArray();
                Assert.HasCount(6, items);
                Assert.IsTrue(items[0].IsEnabled);
                Assert.IsFalse(items[^1].IsEnabled);
                Assert.AreEqual(text, ((SolidColorBrush)items[0].Foreground).Color);

                var rowCenter = items[0].TranslatePoint(new System.Windows.Point(0, items[0].ActualHeight / 2), menu);
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(menu.ActualWidth), (int)Math.Ceiling(menu.ActualHeight),
                    96, 96, PixelFormats.Pbgra32);
                bitmap.Render(menu);
                var pixel = new byte[4];
                bitmap.CopyPixels(new Int32Rect(9, (int)rowCenter.Y, 1, 1), pixel, 4, 0);
                var rendered = Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
                Assert.IsTrue(rendered == panel || rendered == line,
                    $"The left edge of the {(dark ? "dark" : "light")} menu rendered as {rendered}.");
                var separator = menu.Items.OfType<Separator>().Single();
                var rule = (Border)VisualTreeHelper.GetChild(separator, 0);
                Assert.AreEqual(line, ((SolidColorBrush)rule.Background).Color);
                menu.IsOpen = false;
            }
        }
        finally
        {
            menu.IsOpen = false;
            owner.Close();
        }
    });
}
