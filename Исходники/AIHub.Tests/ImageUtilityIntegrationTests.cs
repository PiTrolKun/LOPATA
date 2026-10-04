using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class ImageUtilityIntegrationTests
{
    private string _root = null!;
    [TestInitialize] public void Initialize() => _root = Path.Combine(Path.GetTempPath(), "lopata-utility-ui-" + Guid.NewGuid().ToString("N"));
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [TestMethod]
    public void MethodsHaveTranslationsAndUtilityHasReachableCloudEntrances()
    {
        var node = ScenarioNavigationCatalog.Get(ScenarioNavigationCatalog.ImageUtility);
        Assert.AreEqual(ScenarioNavigationCatalog.Utilities, ScenarioNavigationCatalog.Get(node.ParentId!).ParentId);
        Assert.HasCount(10, ImageUtilityCatalog.Methods);
        Assert.AreEqual(3, ImageUtilityCatalog.Methods.Count(x => x.IsAi));
        foreach (var language in new[] { "ru", "en" })
        {
            var l = new LocalizationService(); l.Load(language);
            foreach (var key in ImageUtilityCatalog.Methods.SelectMany(x => new[] { x.NameKey, x.DescriptionKey }))
                Assert.AreNotEqual(key, l.T(key));
            foreach (var tag in node.Tags.Select(ScenarioNavigationCatalog.GetTag))
            {
                Assert.AreNotEqual(tag.TitleKey, l.T(tag.TitleKey));
                Assert.AreNotEqual(tag.DescriptionKey, l.T(tag.DescriptionKey));
                Assert.IsTrue(ScenarioNavigationCatalog.Get(tag.TargetId).IsAvailable);
            }
        }
        Assert.IsTrue(File.Exists(new ImageUtilityProcessor().ExecutablePath), "The classic engine must ship with the app.");
        Assert.IsTrue(File.Exists(Path.Combine(AppContext.BaseDirectory, "Tools", "image-utility-ai-manifest.json")));
    }

    [TestMethod]
    [DataRow("ru", true, 1500, 900)]
    [DataRow("en", false, 980, 700)]
    public Task FormatOnlyAndLocalizationPreserveExpertChoicesAndQueue(string language, bool dark, int width, int height) => OnUi(async () =>
    {
        var store = new ImageUtilityStore(_root);
        var preferences = new ImageUtilityPreferences
        {
            Options = new() { MethodId = "lanczos3", Preset = 1080, ExportFolder = Path.Combine(_root, "output"), Parameters = new() { ["lobes"] = "5" } }
        };
        store.SavePreferences(preferences);
        var l = new LocalizationService(); l.Load(language);
        using var ai = new ImageUtilityAiService(new ManagedModelLibraryStore(Path.Combine(_root, "models")));
        var control = new ImageUtilityControl(store, ai); Theme(control, dark);
        try
        {
            control.Configure(l.T, new StorageSettings(), 4, _root);
            await WaitFor(() => Element<ComboBox>(control, "Format").Items.Count > 0);
            Assert.AreEqual("5", Element<TextBox>(control, "DetailRange").Text);
            var tree = Element<TreeView>(control, "Sources");
            if (tree.Items.Count > 0) ((TreeViewItem)tree.Items[0]).IsSelected = true;
            Assert.AreEqual(0, Element<ListBox>(control, "Queue").Items.Count, "Navigation must not enqueue a source.");
            Element<CheckBox>(control, "FormatOnly").IsChecked = true;
            Assert.IsFalse(Element<ComboBox>(control, "Resolution").IsEnabled);
            Assert.IsFalse(Element<Button>(control, "ChooseMethod").IsEnabled);
            Assert.IsFalse(Elements(control).Any(e => AutomationProperties.GetAutomationId(e) == "ImageUtility.Sharpen"));
            Element<CheckBox>(control, "FormatOnly").IsChecked = false;
            Assert.AreEqual("5", Element<TextBox>(control, "DetailRange").Text);
            control.Localize(l.T);
            Assert.AreEqual("5", Element<TextBox>(control, "DetailRange").Text);
            Assert.AreEqual("5", store.LoadPreferences().Options.Parameters["lobes"]);
            Assert.IsTrue(Element<RichTextBox>(control, "Events").IsReadOnly);
            control.Measure(new Size(width, height)); control.Arrange(new Rect(0, 0, width, height)); control.UpdateLayout();
            Assert.IsTrue(Element<ListBox>(control, "Queue").ActualWidth > 120);
            Assert.IsTrue(Element<TreeView>(control, "Sources").ActualWidth > 100);
            Preview(control, language + (dark ? "-dark" : "-light"), width, height, dark);
        }
        finally { control.DisposeRuntime(); }
    });

    [TestMethod]
    public Task QueueImportProcessingAndNextBatchPreserveOriginalsAndHistory() => OnUi(async () =>
    {
        Directory.CreateDirectory(_root);
        var good = Path.Combine(_root, "sample.png");
        var bad = Path.Combine(_root, "broken.png");
        File.WriteAllText(bad, "not an image");
        var pixels = Enumerable.Repeat((byte)180, 64 * 48 * 3).ToArray();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(64, 48, 96, 96, PixelFormats.Rgb24, null, pixels, 64 * 3)));
        using (var stream = File.Create(good)) encoder.Save(stream);
        var original = File.ReadAllBytes(good);
        var store = new ImageUtilityStore(Path.Combine(_root, "state"));
        store.SavePreferences(new() { Options = new() { FormatOnly = true, ExportFolder = Path.Combine(_root, "output") } });
        var l = new LocalizationService(); l.Load("ru");
        using var ai = new ImageUtilityAiService(new ManagedModelLibraryStore(Path.Combine(_root, "models")));
        var control = new ImageUtilityControl(store, ai); Theme(control, true);
        var previousController = ApplicationBackgroundOperations.Current;
        ApplicationBackgroundOperations.Current = null;
        try
        {
            control.Configure(l.T, new StorageSettings(), 4, _root);
            await WaitFor(() => Element<ComboBox>(control, "Format").Items.Count > 0);
            await Invoke(control, "AddSourcesAsync", (object)new string[] { good, bad, good });
            var job = Job(control);
            Assert.HasCount(3, job.Items);
            Assert.AreEqual(ImageUtilityItemStatus.Pending, job.Items[1].Status, "Inspection must not consume processing attempts.");
            Assert.AreEqual(ImageUtilityItemStatus.Duplicate, job.Items[2].Status);
            await Invoke(control, "StartAsync");
            Assert.AreEqual(ImageUtilityItemStatus.Completed, job.Items[0].Status);
            Assert.AreEqual(ImageUtilityItemStatus.Failed, job.Items[1].Status);
            Assert.AreEqual(4, job.Items[1].Attempts);
            Assert.IsTrue(File.Exists(job.Items[0].OutputPath));
            CollectionAssert.AreEqual(original, File.ReadAllBytes(good));
            control.Measure(new Size(1500, 900)); control.Arrange(new Rect(0, 0, 1500, 900)); control.UpdateLayout();
            Preview(control, "ru-dark-queue", 1500, 900, true);
            var previousId = job.Id; var previousOutput = job.OutputFolder;
            await Invoke(control, "AddSourcesAsync", (object)new string[] { good, bad });
            var next = Job(control);
            Assert.AreNotEqual(previousId, next.Id);
            Assert.HasCount(2, next.Items);
            Assert.IsNull(next.OutputFolder);
            Assert.AreEqual(ImageUtilityItemStatus.Completed, store.LoadJob(previousId)!.Items[0].Status);
            await Invoke(control, "StartAsync");
            Assert.AreNotEqual(previousOutput, next.OutputFolder);
            Assert.IsTrue(File.Exists(job.Items[0].OutputPath));
        }
        finally { ApplicationBackgroundOperations.Current = previousController; control.DisposeRuntime(); }
    });

    private static Task Invoke(object control, string name, params object[] arguments) => (Task)control.GetType()
        .GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(control, arguments)!;
    private static ImageUtilityJob Job(object control) => (ImageUtilityJob)control.GetType()
        .GetField("_job", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(control)!;

    private static T Element<T>(DependencyObject control, string suffix) where T : DependencyObject =>
        Elements(control).OfType<T>().Single(x => AutomationProperties.GetAutomationId(x) == "ImageUtility." + suffix);
    private static IEnumerable<DependencyObject> Elements(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var item in Elements(child)) yield return item;
    }
    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.IsTrue(condition());
    }
    private static Task OnUi(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); done.SetResult(); }
                catch (Exception error) { done.SetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task;
    }
    private static void Theme(FrameworkElement control, bool dark)
    {
        control.Resources["TextPrimaryBrush"] = dark ? Brushes.WhiteSmoke : Brushes.Black;
        control.Resources["TextSecondaryBrush"] = dark ? Brushes.LightSlateGray : Brushes.DimGray;
        control.Resources["PanelBrush"] = dark ? new SolidColorBrush(Color.FromRgb(23, 32, 49)) : Brushes.WhiteSmoke;
        control.Resources["WindowBackgroundBrush"] = dark ? new SolidColorBrush(Color.FromRgb(16, 24, 39)) : Brushes.White;
        control.Resources["SecondaryButtonBackgroundBrush"] = control.Resources["PanelBrush"];
        control.Resources["LineBrush"] = Brushes.SlateGray; control.Resources["AccentBrush"] = Brushes.RoyalBlue;
        control.Resources["UiBodyFontSize"] = 14d;
    }
    private static void Preview(FrameworkElement control, string name, int width, int height, bool dark)
    {
        var directory = Environment.GetEnvironmentVariable("LOPATA_UTILITY_PREVIEWS");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var dc = background.RenderOpen()) dc.DrawRectangle((Brush)control.Resources["WindowBackgroundBrush"], null, new Rect(0, 0, width, height));
        bitmap.Render(background); bitmap.Render(control);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(output);
    }
}
