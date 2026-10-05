using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicEditorTests
{
    [TestMethod]
    public void FivePercentReserveAndExactBoundaryAreEnforced()
    {
        var tokenizer = new CharacterTokenizer();
        var empty = MusicTextBudget.Measure(tokenizer, "");
        var atLimit = MusicTextBudget.Measure(tokenizer, new string('a', empty.LyricsBudget));
        Assert.AreEqual(23347, atLimit.TotalTokens); Assert.IsFalse(atLimit.Exceeded);
        Assert.IsTrue(MusicTextBudget.CanStart(atLimit, false, "a"));
        var excess = MusicTextBudget.Measure(tokenizer, new string('a', empty.LyricsBudget + 1));
        Assert.IsTrue(excess.Exceeded); Assert.IsFalse(MusicTextBudget.CanStart(excess, false, "a"));
        Assert.IsFalse(MusicTextBudget.CanStart(atLimit, true, "a"));
        Assert.IsFalse(MusicTextBudget.CanStart(null, false, "a"));
        Assert.IsFalse(MusicTextBudget.CanStart(atLimit, false, " \n"));
        var changed = MusicTextBudget.Measure(tokenizer, "", tags: "123", outputReserve: 4000);
        Assert.AreEqual(empty.LyricsBudget - 4003, changed.LyricsBudget);
        Assert.AreEqual(empty.TotalTokens + 3, changed.TotalTokens); // Future output is not reported as already used.
        Assert.Throws<ArgumentOutOfRangeException>(() => MusicTextBudget.Measure(tokenizer, "", outputReserve: -1));
        Assert.AreEqual(2, MusicTextBudget.CharacterCount("о́🌟"));
    }

    [TestMethod]
    public void WholeRequestIsCountedSeparatelyFromItsParts()
    {
        var tokenizer = new BoundaryTokenizer();
        var usage = MusicTextBudget.Measure(tokenizer, "lyrics");
        Assert.AreEqual(7, usage.TotalTokens); Assert.AreEqual(1, usage.LyricsTokens);
        Assert.AreEqual(MusicTextBudget.FillLimit - 5, usage.LyricsBudget);
    }

    [TestMethod]
    public void InstalledGgufMatchesIndependentTiktokenReferenceIds()
    {
        var path = Environment.GetEnvironmentVariable("LOPATA_MUSIC_TOKENIZER_PATH");
        if (string.IsNullOrEmpty(path)) Assert.Inconclusive("Set LOPATA_MUSIC_TOKENIZER_PATH to the user-installed GGUF for tokenizer parity.");
        var metadata = MusicTokenizerMetadata.Read(path!); Assert.AreEqual(24576, metadata.ContextSize);
        Assert.AreEqual(151851, metadata.Tokens.Length); Assert.AreEqual(151387, metadata.Merges.Length);
        var tokenizer = new MusicTokenizer(metadata);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Music", "tokenizer-reference.json")));
        foreach (var vector in json.RootElement.GetProperty("vectors").EnumerateArray())
        {
            var text = vector.GetProperty("text").GetString()!;
            var expected = vector.GetProperty("ids").EnumerateArray().Select(v => v.GetInt32()).ToArray();
            CollectionAssert.AreEqual(expected, tokenizer.Encode(text), text[..Math.Min(text.Length, 90)]);
        }
        var usage = MusicTextBudget.Measure(tokenizer, "");
        Assert.AreEqual(29, usage.TotalTokens); Assert.AreEqual(23318, usage.LyricsBudget);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => tokenizer.Count("текст", cancel.Token));
        Assert.Throws<OperationCanceledException>(() => MusicTokenizerMetadata.Read(path!, cancel.Token));
    }

    [TestMethod]
    public void MalformedMetadataCannotBeMistakenForReadyTokenizer()
    {
        var path = Path.GetTempFileName();
        try
        {
            using (var stream = File.Create(path))
            using (var writer = new BinaryWriter(stream))
            { writer.Write(0x46554747u); writer.Write(3u); writer.Write(0ul); writer.Write(100001ul); }
            Assert.Throws<InvalidDataException>(() => MusicTokenizerMetadata.Read(path));
            File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6, 7, 8]);
            Assert.Throws<InvalidDataException>(() => MusicTokenizerMetadata.Read(path));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public Task EditorKeepsOverflowTextUndoAndThemeAndRejectsPendingCounts() => ScenarioNavigationTests.Sta(() =>
    {
        using var editor = new MusicLyricsEditor((_, _) => Task.FromResult<IMusicTokenizer>(new CharacterTokenizer()));
        editor.Resources["TextPrimaryBrush"] = Brushes.Black;
        editor.Resources["TextSecondaryBrush"] = Brushes.Gray;
        var localizer = new LocalizationService(); localizer.Load("ru"); editor.Localize(localizer.T);
        Pump(editor.LoadTokenizerAsync("test-model"));
        var box = ScenarioNavigationTests.LogicalDescendants(editor).OfType<System.Windows.Controls.TextBox>().Single();
        var window = new Window { Content = editor, Width = 420, Height = 500, Left = -10000, Top = -10000,
            ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
        window.Show(); Drain();
        try
        {
        box.Text = new string('a', editor.Usage!.LyricsBudget + 1);
        Assert.IsFalse(editor.TryGetGenerationText(out _)); Pump(editor.RefreshCountsAsync());
        Assert.IsTrue(editor.Usage!.Exceeded); Assert.AreEqual(Brushes.Red, box.Foreground);
        Assert.IsTrue(box.IsUndoEnabled); Assert.AreEqual(0, box.MaxLength);
        var length = box.Text.Length; box.Select(length - 1, 1); box.SelectedText = "";
        Assert.IsFalse(editor.CanGenerate); Pump(editor.RefreshCountsAsync());
        Assert.IsTrue(editor.CanGenerate); Assert.AreEqual(Brushes.Black, box.Foreground);
        Assert.IsTrue(editor.TryGetGenerationText(out var text)); Assert.AreEqual(length - 1, text.Length);
        box.Undo(); Pump(editor.RefreshCountsAsync());
        Assert.AreEqual(length, box.Text.Length); Assert.IsFalse(editor.CanGenerate);
        box.Select(length - 1, 1); box.SelectedText = ""; Pump(editor.RefreshCountsAsync());
        editor.Resources["TextPrimaryBrush"] = Brushes.White; localizer.Load("en"); editor.Localize(localizer.T);
        Assert.AreEqual(Brushes.White, box.Foreground); Assert.AreEqual(length - 1, box.Text.Length);
        editor.ConfigureRequest("extra", MusicTextBudget.DefaultInstruction); Pump(editor.RefreshCountsAsync());
        Assert.IsTrue(editor.Usage!.Exceeded); // Program data also invalidates an otherwise full editor.
        editor.Dispose(); Assert.IsFalse(editor.TryGetGenerationText(out _));
        }
        finally { window.Close(); }
    });

    [TestMethod]
    public Task UnavailableTokenizerAndStaleLoadCannotEnableGeneration() => ScenarioNavigationTests.Sta(() =>
    {
        var first = new TaskCompletionSource<IMusicTokenizer>();
        using var editor = new MusicLyricsEditor((path, _) => path == "slow" ? first.Task : Task.FromException<IMusicTokenizer>(new InvalidDataException()));
        editor.Lyrics = "сохранить этот текст";
        var slow = editor.LoadTokenizerAsync("slow"); Drain();
        Pump(editor.LoadTokenizerAsync("broken"));
        first.SetResult(new CharacterTokenizer()); Pump(slow);
        Assert.IsNull(editor.Usage); Assert.IsFalse(editor.TryGetGenerationText(out _));
        Assert.AreEqual("сохранить этот текст", editor.Lyrics);
    });

    [TestMethod]
    public Task ResizingKeepsFooterPinnedAndTextIntact() => ScenarioNavigationTests.Sta(() =>
    {
        using var workspace = new MusicWorkspaceControl();
        var localizer = new LocalizationService(); localizer.Load("ru"); workspace.Localize(localizer.T);
        workspace.Editor.Lyrics = string.Join("\n", Enumerable.Repeat("Длинная строка песни", 200));
        var grid = (Grid)workspace.Content;
        var divider = ScenarioNavigationTests.LogicalDescendants(workspace).OfType<Thumb>().Single();
        for (var width = 1040; width <= 1600; width += 7)
        {
            workspace.Measure(new(width, 600)); workspace.Arrange(new(0, 0, width, 600)); workspace.UpdateLayout();
            divider.RaiseEvent(new DragDeltaEventArgs(3, 0) { RoutedEvent = Thumb.DragDeltaEvent });
            workspace.UpdateLayout(); var expectedWidth = grid.ColumnDefinitions[0].ActualWidth;
            workspace.UpdateLayout(); Assert.AreEqual(expectedWidth, grid.ColumnDefinitions[0].ActualWidth);
            foreach (var counter in ScenarioNavigationTests.LogicalDescendants(workspace.Editor).OfType<TextBlock>()
                .Where(t => AutomationProperties.GetAutomationId(t).EndsWith("Count", StringComparison.Ordinal)))
            {
                var bounds = counter.TransformToAncestor(workspace.Editor).TransformBounds(new(0, 0, counter.ActualWidth, counter.ActualHeight));
                Assert.IsTrue(bounds.Bottom <= workspace.Editor.ActualHeight + 1);
                Assert.IsTrue(counter.ActualHeight > 0);
            }
        }
        Assert.AreEqual(200, workspace.Editor.Lyrics.Split('\n').Length);
    });

    private static void Pump(Task task)
    {
        var dispatcher = Dispatcher.CurrentDispatcher; var frame = new DispatcherFrame();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var registration = timeout.Token.Register(() => dispatcher.BeginInvoke(() => frame.Continue = false));
        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        if (!task.IsCompleted) throw new TimeoutException("Editor task did not complete.");
        task.GetAwaiter().GetResult();
    }
    private static void Drain()
    {
        var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
    private sealed class CharacterTokenizer : IMusicTokenizer
    { public int Count(string text, CancellationToken cancellation = default) { cancellation.ThrowIfCancellationRequested(); return text.Length; } }
    private sealed class BoundaryTokenizer : IMusicTokenizer
    { public int Count(string text, CancellationToken cancellation = default) => text == "lyrics" ? 1 : text.EndsWith("lyrics\n", StringComparison.Ordinal) ? 5 : 3; }
}
