using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Services;
using AIHub.Services.Books.Smart;

namespace AIHub.Tests;

public sealed partial class BookConversionUiTests
{
    [TestMethod, TestCategory("Native")]
    public Task AlreadyPreparedSmartModeDoesNotOpenPreparationAgain() => Sta(async () =>
    {
        if (Environment.GetEnvironmentVariable("LOPATA_BOOK_SMART_CONTEXT_NATIVE") != "1")
            Assert.Inconclusive("Explicit installed-component readiness regression opt-in required; no download or model launch.");
        var storage = new StorageSettingsStore().LoadOrCreate();
        var root = new CoreModelManager().GetModelsRoot(storage)!;
        using var service = new BookSmartPreparationService(root, false, key => key, BookSmartOcrCatalog.CreateCards);
        var selection = new BookSmartSelection();
        var all = service.VerifiedPaths(selection, true);
        var files = all ?? service.VerifiedPaths(selection, false);
        Assert.IsNotNull(files, "Native readiness fixture requires already verified installed components.");
        var paths = files.Concat(Directory.EnumerateFiles(BookSmartOcrRuntime.DirectoryFor(selection), "*", SearchOption.AllDirectories))
            .Append(BookSmartOcrRuntime.WorkerPath).ToArray();
        var owner = new Window(); Theme(owner, true); owner.Show();
        var window = new BookConversionWindow(owner, key => key);
        window.ConfigureSmart(new(new(), new()), storage, "ru");
        typeof(BookConversionWindow).GetField("_smartSelection", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, selection);
        typeof(BookConversionWindow).GetField("_preparedSmartSession", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window,
            new BookSmartPreparedSession(root, selection, all is not null, EngineSetSelection.ActiveId, paths));
        var opened = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            foreach (var dialog in window.OwnedWindows.OfType<BookSmartPreparationWindow>().ToArray())
            { opened = true; dialog.Close(); }
        };
        try
        {
            window.Show(); timer.Start();
            if (Environment.GetEnvironmentVariable("LOPATA_BOOK_SMART_REGRESSION_SOURCE") is { } source)
                await Invoke(window, "AddPathsAsync", (object)new[] { source });
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var result = await (Task<bool>)typeof(BookConversionWindow).GetMethod("PrepareSmartAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(window, [false])!;
                Assert.IsTrue(result); Assert.IsFalse(opened, "Converting in an already prepared mode must not reopen preparation.");
            }
        }
        finally { timer.Stop(); window.Close(); owner.Close(); }
    });
}
