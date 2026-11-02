using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Services;
using AIHub.Services.SongClip;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class SongClipLayoutTests
{
    private const double Tolerance = 2;
    private static readonly string[] Areas = ["Lyrics", "Reference", "Parameters", "Timings", "Preview", "Results", "Output"];
    private static readonly string[] Actions = ["ImportSong", "ImportReference", "RemoveReference", "Components", "Generate", "Pause", "Stop", "Export", "Editor", "Play", "Fullscreen"];

    [TestMethod]
    [DataRow("ru", 1900d, 980d, 1d)]
    [DataRow("en", 1900d, 980d, 1d)]
    [DataRow("ru", 1900d, 980d, 1.4d)]
    [DataRow("en", 1900d, 980d, 1.4d)]
    [DataRow("ru", 860d, 560d, 1d)]
    [DataRow("en", 860d, 560d, 1d)]
    [DataRow("ru", 860d, 560d, 1.4d)]
    [DataRow("en", 860d, 560d, 1.4d)]
    public Task SketchAreasAndActionsFitTheAvailableWorkspace(string language, double width, double height, double scale) => OnUi(() =>
    {
        using var files = new VideoPreparationTests.TestFiles();
        var store = new SongClipStore(files.File("song-layout-projects"));
        var project = new SongClipProject
        {
            Name = "A long project title / Длинное название проекта для проверки интерфейса",
            Lyrics = string.Join('\n', Enumerable.Repeat("Long song lyrics remain editable. Длинная строка текста песни.", 60)),
            Timings = string.Join('\n', Enumerable.Range(0, 50).Select(i => $"[{i / 60:00}:{i % 60:00}.00] A retained line / Строка песни"))
        };
        store.Save(project);
        var localizer = new LocalizationService(); localizer.Load(language);
        var control = new SongClipControl(localizer.T, store);
        control.OpenProject(project.Id);
        var window = CreateHost(control, width, height, scale, out var workspace);
        try
        {
            window.Show(); FlushLayout(window);
            PopulateResultAndLogText(control);
            FlushLayout(window);
            var scroll = Find<ScrollViewer>(control, "WorkspaceScroll");
            Assert.IsTrue(double.IsFinite(scroll.ViewportWidth) && scroll.ViewportWidth > 0, "The workspace must have a finite horizontal viewport.");
            Assert.IsTrue(double.IsFinite(scroll.ViewportHeight) && scroll.ViewportHeight > 0, "The workspace must have a finite vertical viewport.");
            Assert.IsTrue(scroll.ActualWidth <= workspace.ActualWidth + Tolerance, "The workspace must use its host width, rather than measuring outside the host.");
            Assert.IsTrue(scroll.ActualHeight < height - (96 * scale), "Header, footer and page margins must reduce the available workspace height.");
            AssertSketchGeometry(control);
            WriteEvidence(window, control, scroll, language, width, height, scale, "initial");

            if (width > 1280)
            {
                Assert.IsTrue(scroll.ScrollableWidth <= Tolerance, "The normal workspace must use the available width without horizontal scrolling.");
                foreach (var action in Actions) AssertActionVisible(Find<Button>(control, action), control);
                AssertFullyVisible(Find<Button>(control, "Model"), control);
                AssertFullyVisible(Find<ProgressBar>(control, "Progress"), control);
            }
            else
            {
                Assert.IsTrue(scroll.ScrollableWidth > 0, "The compact host must expose horizontal scrolling for the full workspace.");
                Assert.IsTrue(scroll.ScrollableHeight > 0, "The compact host must expose vertical scrolling below the header and project toolbar.");
                foreach (var action in Actions)
                {
                    var button = Find<Button>(control, action);
                    button.BringIntoView(); FlushLayout(window);
                    AssertActionVisible(button, control);
                }
                var model = Find<Button>(control, "Model");
                model.BringIntoView(); FlushLayout(window); AssertFullyVisible(model, control);
                scroll.ScrollToRightEnd(); scroll.ScrollToBottom(); FlushLayout(window);
                Assert.AreEqual(scroll.ScrollableWidth, scroll.HorizontalOffset, Tolerance, "The far right of the workspace must be reachable.");
                Assert.AreEqual(scroll.ScrollableHeight, scroll.VerticalOffset, Tolerance, "The bottom of the workspace must be reachable.");
                AssertFullyVisible(Find<ProgressBar>(control, "Progress"), control);
                WriteEvidence(window, control, scroll, language, width, height, scale, "bottom-right");
            }
        }
        finally { window.Close(); }
    });

    private static Window CreateHost(SongClipControl control, double width, double height, double scale, out Grid workspace)
    {
        var host = new Grid();
        host.RowDefinitions.Add(new() { Height = new GridLength(64 * scale) });
        host.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        host.RowDefinitions.Add(new() { Height = new GridLength(32 * scale) });
        host.Children.Add(new Border { Background = Brushes.Black, Child = new TextBlock { Text = "LOPATA", VerticalAlignment = VerticalAlignment.Center } });
        workspace = new Grid { Margin = new Thickness(18, 16, 18, 16) };
        workspace.Children.Add(control); Grid.SetRow(workspace, 1); host.Children.Add(workspace);
        var footer = new Border { Background = Brushes.Black, Child = new TextBlock { Text = "Status", VerticalAlignment = VerticalAlignment.Center } };
        Grid.SetRow(footer, 2); host.Children.Add(footer);
        var window = new Window
        {
            Content = host, Width = width, Height = height, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, ShowActivated = false, UseLayoutRounding = true,
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI")
        };
        foreach (var (key, value) in new[]
        {
            ("UiBodyFontSize", 14d), ("UiButtonFontSize", 14d), ("UiSmallFontSize", 12d), ("UiTinyFontSize", 11d),
            ("UiSectionFontSize", 18d), ("UiCardTitleFontSize", 20d), ("UiPageTitleFontSize", 32d), ("UiHeaderTitleFontSize", 18d)
        }) window.Resources[key] = value * scale;
        foreach (var value in new[] { 10d, 11d, 12d, 13d, 14d, 15d, 16d, 17d, 18d, 19d, 20d, 21d, 22d, 26d, 30d, 32d, 34d })
            window.Resources["UiFont" + value.ToString(CultureInfo.InvariantCulture)] = value * scale;
        foreach (var value in new[] { 16d, 17d, 18d, 19d, 20d, 21d, 22d, 24d })
            window.Resources["UiLineHeight" + value.ToString(CultureInfo.InvariantCulture)] = value * scale;
        window.Resources["UiButtonHeight"] = 40 * scale;
        window.Resources["UiPrimaryButtonMinWidth"] = 156 * Math.Min(scale, 1.25);
        window.Resources["UiSecondaryButtonMinWidth"] = 96 * Math.Min(scale, 1.25);
        window.Resources["WindowBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(15, 23, 38));
        window.Resources["PanelBrush"] = new SolidColorBrush(Color.FromRgb(23, 32, 50));
        window.Resources["SecondaryButtonBackgroundBrush"] = window.Resources["PanelBrush"];
        window.Resources["InputBrush"] = window.Resources["PanelBrush"];
        window.Resources["TextPrimaryBrush"] = Brushes.White;
        window.Resources["TextSecondaryBrush"] = Brushes.LightGray;
        window.Resources["LineBrush"] = Brushes.DimGray;
        window.Resources["AccentBrush"] = Brushes.RoyalBlue;
        window.Resources["StepBadgeBrush"] = Brushes.DarkSlateBlue;
        window.SetResourceReference(Window.FontSizeProperty, "UiBodyFontSize");
        window.SetResourceReference(Window.BackgroundProperty, "WindowBackgroundBrush");
        window.SetResourceReference(Window.ForegroundProperty, "TextPrimaryBrush");
        return window;
    }

    private static void PopulateResultAndLogText(SongClipControl control)
    {
        var results = VisualDescendants(Find<FrameworkElement>(control, "Layout.Results")).OfType<ListBox>().Single();
        results.ItemsSource = Enumerable.Range(1, 12).Select(i => new ListBoxItem
        {
            Content = $"Glitchframe · clip {i} · 240.0 s · 31.10.2026 12:34 · Длинное название готового клипа"
        }).ToArray();
        Find<TextBox>(control, "Log").Text = string.Join('\n', Enumerable.Repeat("Stage 12/20: assembling the complete song / Сборка полного клипа.", 40));
    }

    private static void AssertSketchGeometry(SongClipControl control)
    {
        var areas = Areas.ToDictionary(name => name, name => Bounds(Find<FrameworkElement>(control, "Layout." + name), control));
        foreach (var (name, rect) in areas) Assert.IsTrue(rect.Width > 30 && rect.Height > 30, $"{name} must have usable space: {rect}.");
        var lyrics = areas["Lyrics"]; var reference = areas["Reference"]; var parameters = areas["Parameters"];
        var timings = areas["Timings"]; var preview = areas["Preview"]; var results = areas["Results"]; var output = areas["Output"];
        AssertLeftOf(lyrics, reference, "Lyrics must precede the reference.");
        AssertLeftOf(reference, parameters, "Parameters must occupy the large upper area to the right of the reference.");
        Assert.AreEqual(lyrics.Top, reference.Top, Tolerance); Assert.AreEqual(reference.Top, parameters.Top, Tolerance);
        Assert.AreEqual(lyrics.Left, timings.Left, Tolerance, "Timings must be below the lyrics.");
        Assert.AreEqual(reference.Left, preview.Left, Tolerance, "Preview must begin below the reference column.");
        Assert.IsTrue(timings.Top > lyrics.Bottom, "The import row must separate lyrics and timings.");
        Assert.IsTrue(preview.Top > reference.Bottom, "The import row must separate reference and preview.");
        AssertLeftOf(timings, preview, "Timings must be left of the preview.");
        AssertLeftOf(preview, results, "The results list must be adjacent to the preview.");
        AssertLeftOf(results, output, "Output and logs must have a separate area to the right of the results list.");
        Assert.IsTrue(preview.Width > results.Width * 1.5, "The preview must be appreciably wider than the narrow results list.");
        Assert.AreEqual(timings.Top, preview.Top, Tolerance); Assert.AreEqual(preview.Top, results.Top, Tolerance);
        Assert.IsTrue(output.Top >= parameters.Bottom - Tolerance && output.Top < preview.Top, "Output settings must start in the middle row and continue alongside the bottom row.");
        Assert.IsTrue(output.Bottom >= results.Bottom - Tolerance, "The output column must reach the bottom of the workspace.");

        AssertBetweenRows(Bounds(Find<Button>(control, "ImportSong"), control), lyrics, timings, "Add song");
        AssertBetweenRows(Bounds(Find<Button>(control, "ImportReference"), control), reference, preview, "Add reference");
        var model = Bounds(Find<Button>(control, "Model"), control);
        Assert.IsTrue(model.Top >= parameters.Bottom - Tolerance && model.Bottom <= preview.Top + Tolerance, "Model selection must be under the parameters, above the preview.");
        Assert.IsTrue(model.Left >= parameters.Left - Tolerance && model.Right <= results.Left + Tolerance, "Model selection must occupy the middle column.");
        foreach (var id in new[] { "Generate", "Pause", "Stop" })
        {
            var action = Bounds(Find<Button>(control, id), control);
            Assert.IsTrue(action.Top >= parameters.Bottom - Tolerance && action.Bottom <= results.Top + Tolerance, $"{id} must be in the middle generation row.");
            Assert.IsTrue(action.Left >= results.Left - Tolerance && action.Right <= output.Left + Tolerance, $"{id} must remain beside model selection, above results.");
        }
        var log = Bounds(Find<TextBox>(control, "Log"), control);
        var progress = Bounds(Find<ProgressBar>(control, "Progress"), control);
        AssertContains(output, log, "The journal must be inside the separate output column.");
        AssertContains(output, progress, "Progress must be inside the separate output column.");
        Assert.IsTrue(log.Bottom <= progress.Top + Tolerance, "Progress must be below the generation journal.");
    }

    private static void AssertBetweenRows(Rect action, Rect above, Rect below, string name)
    {
        Assert.IsTrue(action.Top >= above.Bottom - Tolerance && action.Bottom <= below.Top + Tolerance, $"{name} must be between its upper and lower panels.");
        Assert.IsTrue(action.Left >= above.Left - Tolerance && action.Right <= above.Right + Tolerance, $"{name} must stay in its own column.");
    }

    private static void AssertActionVisible(Button button, FrameworkElement control)
    {
        AssertFullyVisible(button, control);
        foreach (var text in VisualDescendants(button).OfType<TextBlock>().Where(t => t.IsVisible && t.Text.Length > 0))
        {
            AssertFullyVisible(text, control);
            AssertContains(new Rect(button.RenderSize), Bounds(text, button), $"The label of {AutomationProperties.GetAutomationId(button)} must fit inside its button.");
            Assert.AreEqual(TextTrimming.None, text.TextTrimming, "Action labels must remain complete.");
            var (required, available) = MeasureActionLabel(text, button);
            var details = $"Label '{text.Text}' ({AutomationProperties.GetAutomationId(button)}): required {required}; actual {text.RenderSize}; content slot {available}; font {text.FontSize}; language {text.Language}; formatting {TextOptions.GetTextFormattingMode(text)}; DPI {VisualTreeHelper.GetDpi(text).PixelsPerDip}.";
            Assert.IsTrue(required.Width <= available.Width + Tolerance, "Action label width exceeds its content slot. " + details);
            Assert.IsTrue(required.Height <= text.ActualHeight + Tolerance, "Action label height is clipped. " + details);
        }
    }

    private static (Size Required, Size Available) MeasureActionLabel(TextBlock text, Button button)
    {
        ContentPresenter? presenter = null;
        for (var ancestor = VisualTreeHelper.GetParent(text); ancestor is not null && !ReferenceEquals(ancestor, button); ancestor = VisualTreeHelper.GetParent(ancestor))
            if (ancestor is ContentPresenter content) { presenter = content; break; }
        Assert.IsNotNull(presenter, "An action label must have a content presenter.");
        var slot = LayoutInformation.GetLayoutSlot(presenter);
        var margin = presenter.Margin;
        var available = new Size(Math.Max(0, slot.Width - margin.Left - margin.Right), Math.Max(0, slot.Height - margin.Top - margin.Bottom));
        Assert.IsTrue(double.IsFinite(available.Width) && available.Width > 0, "An action label must have a finite content width.");

        // A centered label's ActualWidth is its rounded text width, not the width it was measured against.
        // Reformatting at that tight width can invent an extra line. Use WPF's own text layout at the content slot width.
        var probe = new TextBlock
        {
            Text = text.Text, FontFamily = text.FontFamily, FontSize = text.FontSize, FontStyle = text.FontStyle,
            FontWeight = text.FontWeight, FontStretch = text.FontStretch, FlowDirection = text.FlowDirection,
            Language = text.Language, TextWrapping = text.TextWrapping, TextTrimming = TextTrimming.None,
            TextAlignment = text.TextAlignment, Padding = text.Padding, LineHeight = text.LineHeight,
            LineStackingStrategy = text.LineStackingStrategy, UseLayoutRounding = text.UseLayoutRounding,
            SnapsToDevicePixels = text.SnapsToDevicePixels
        };
        TextOptions.SetTextFormattingMode(probe, TextOptions.GetTextFormattingMode(text));
        TextOptions.SetTextRenderingMode(probe, TextOptions.GetTextRenderingMode(text));
        VisualTreeHelper.SetRootDpi(probe, VisualTreeHelper.GetDpi(text));
        // Layout rounding can round a centered label down by one physical pixel.
        // Preserve that same tolerance when remeasuring, rather than inventing a line break.
        var width = available.Width + 1 / VisualTreeHelper.GetDpi(text).DpiScaleX;
        probe.Measure(new Size(text.TextWrapping == TextWrapping.NoWrap ? double.PositiveInfinity : width, double.PositiveInfinity));
        return (probe.DesiredSize, available);
    }

    private static void AssertFullyVisible(FrameworkElement element, FrameworkElement control)
    {
        var id = AutomationProperties.GetAutomationId(element);
        Assert.IsTrue(element.IsVisible && element.ActualWidth > 0 && element.ActualHeight > 0, $"{id} must be visible and have a nonempty rectangle.");
        AssertContains(new Rect(control.RenderSize), Bounds(element, control), $"{id} must be fully inside the available workspace.");
        for (DependencyObject? ancestor = element; ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor))
        {
            if (ancestor is FrameworkElement visual && LayoutInformation.GetLayoutClip(visual) is { } layoutClip)
                AssertContains(layoutClip.Bounds, Bounds(element, visual), $"{id} must not be cut off by an ancestor layout clip.");
            if (ancestor is ScrollContentPresenter presenter)
                AssertContains(new Rect(presenter.RenderSize), Bounds(element, presenter), $"{id} must be fully visible within its scroll viewport.");
            if (ancestor is UIElement { ClipToBounds: true } clipped)
                AssertContains(new Rect(clipped.RenderSize), Bounds(element, clipped), $"{id} must not be cut off by an ancestor viewport.");
            if (ReferenceEquals(ancestor, control)) break;
        }
    }

    private static void AssertLeftOf(Rect left, Rect right, string message) => Assert.IsTrue(left.Right <= right.Left + Tolerance, message);
    private static void AssertContains(Rect outer, Rect inner, string message) => Assert.IsTrue(
        inner.Left >= outer.Left - Tolerance && inner.Top >= outer.Top - Tolerance && inner.Right <= outer.Right + Tolerance && inner.Bottom <= outer.Bottom + Tolerance,
        $"{message} Outer: {outer}; inner: {inner}.");
    private static Rect Bounds(FrameworkElement element, Visual ancestor) => ReferenceEquals(element, ancestor)
        ? new Rect(element.RenderSize) : element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));

    private static T Find<T>(DependencyObject root, string id) where T : FrameworkElement => VisualDescendants(root).OfType<T>()
        .Single(element => AutomationProperties.GetAutomationId(element) == "SongClip." + id);
    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in VisualDescendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static void FlushLayout(Window window)
    {
        window.UpdateLayout();
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame); window.UpdateLayout();
    }

    private static void WriteEvidence(Window window, SongClipControl control, ScrollViewer scroll, string language, double width, double height, double scale, string position)
    {
        var directory = Environment.GetEnvironmentVariable("LOPATA_GLITCHFRAME_TEST_EVIDENCE");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var name = FormattableString.Invariant($"layout-{language}-{width:0}x{height:0}-{scale * 100:0}-{position}");
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(directory, name + ".png"))) png.Save(file);
        File.WriteAllText(Path.Combine(directory, name + ".json"), JsonSerializer.Serialize(new
        {
            language, scale, width = window.ActualWidth, height = window.ActualHeight,
            scroll.ViewportWidth, scroll.ViewportHeight, scroll.ExtentWidth, scroll.ExtentHeight, scroll.HorizontalOffset, scroll.VerticalOffset,
            areas = Areas.ToDictionary(area => area, area => Bounds(Find<FrameworkElement>(control, "Layout." + area), control).ToString(CultureInfo.InvariantCulture)),
            labels = Actions.SelectMany(action =>
            {
                var button = Find<Button>(control, action);
                return VisualDescendants(button).OfType<TextBlock>().Where(text => text.IsVisible && text.Text.Length > 0).Select(text =>
                {
                    var (required, available) = MeasureActionLabel(text, button);
                    return new
                    {
                        action, text = text.Text, text.ActualWidth, text.ActualHeight, requiredWidth = required.Width, requiredHeight = required.Height,
                        availableWidth = available.Width, availableHeight = available.Height, text.FontSize,
                        language = text.Language.IetfLanguageTag, formatting = TextOptions.GetTextFormattingMode(text).ToString(), dpi = VisualTreeHelper.GetDpi(text).PixelsPerDip
                    };
                });
            }).ToArray()
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static Task OnUi(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception error) { completion.SetException(error); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(40));
    }
}
