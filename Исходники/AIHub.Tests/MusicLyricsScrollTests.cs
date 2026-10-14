using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AIHub.Controls;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicLyricsScrollTests
{
    [TestMethod]
    public Task FollowShowsOffscreenPhraseWithoutChangingTextCaretOrUndo() => ScenarioNavigationTests.Sta(() => {
        using var editor = new MusicLyricsEditor();
        var box = Box(editor); editor.Lyrics = string.Join("\n", Enumerable.Range(0, 80).Select(i => $"Строка {i}: кот зовёт солнце."));
        var window = new Window { Width = 360, Height = 350, Content = editor, ShowInTaskbar = false };
        try {
            window.Show(); Layout(window); box.Select(2, 4);
            var original = editor.Lyrics; var undo = box.CanUndo;
            var start = original.IndexOf("Строка 65:", StringComparison.Ordinal);
            editor.SetPlaybackHighlight(start, "Строка 65: кот зовёт солнце.".Length); Layout(window);
            Assert.IsTrue(box.VerticalOffset > 0);
            var rect = box.GetRectFromCharacterIndex(start);
            Assert.IsFalse(rect.IsEmpty); Assert.IsTrue(rect.Top >= 0 && rect.Bottom <= box.ViewportHeight);
            Assert.AreEqual(original, editor.Lyrics); Assert.AreEqual(2, box.SelectionStart); Assert.AreEqual(4, box.SelectionLength);
            Assert.AreEqual(undo, box.CanUndo);
        }
        finally { window.Close(); }
    });

    [TestMethod]
    public Task PhraseBelowTheBottomEdgeMovesEvenWhenItsPrecedingRowsAreVisible() => ScenarioNavigationTests.Sta(() => {
        using var editor = new MusicLyricsEditor(); var box = Box(editor);
        editor.Lyrics = string.Join("\n", Enumerable.Range(0, 80).Select(i => $"Строка {i}: кот зовёт солнце."));
        var window = new Window { Width = 360, Height = 350, Content = editor, ShowInTaskbar = false };
        try {
            window.Show(); Layout(window);
            var line = box.GetLastVisibleLineIndex() + 1;
            var start = box.GetCharacterIndexFromLineIndex(line);
            var before = box.VerticalOffset;
            editor.SetPlaybackHighlight(start, box.GetLineLength(line) - 1); Layout(window);
            var rect = box.GetRectFromCharacterIndex(start);
            Assert.IsTrue(box.VerticalOffset > before, "An already-visible preceding row prevented scroll.");
            Assert.IsTrue(!rect.IsEmpty && rect.Top >= 0 && rect.Bottom <= box.ViewportHeight);
        }
        finally { window.Close(); }
    });

    [TestMethod]
    public Task NextPhraseReturnsAfterManualScrollingAndPauseAllowsFreeReading() => ScenarioNavigationTests.Sta(() => {
        using var editor = new MusicLyricsEditor(); var box = Box(editor);
        editor.Lyrics = string.Join("\n", Enumerable.Range(0, 80).Select(i => $"Строка {i}: кот зовёт солнце."));
        var window = new Window { Width = 360, Height = 350, Content = editor, ShowInTaskbar = false };
        try {
            window.Show(); Layout(window);
            var start = editor.Lyrics.IndexOf("Строка 60:", StringComparison.Ordinal);
            editor.SetPlaybackHighlight(start, 20); Layout(window);
            box.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
            box.ScrollToHome(); Layout(window); var readingOffset = box.VerticalOffset;
            editor.SetPlaybackHighlight(start, 20); Layout(window);
            Assert.AreEqual(readingOffset, box.VerticalOffset, "Playback fought a user's reading gesture.");
            var next = editor.Lyrics.IndexOf("Строка 75:", StringComparison.Ordinal);
            editor.SetPlaybackHighlight(next, 20); Layout(window);
            var rect = box.GetRectFromCharacterIndex(next);
            Assert.IsTrue(!rect.IsEmpty && rect.Top >= 0 && rect.Bottom <= box.ViewportHeight, "The next phrase must resume following automatically.");
            editor.SetPlaybackHighlight(next, 20, playing: false); box.ScrollToHome(); Layout(window);
            editor.ResumePlaybackFollow(); editor.SetPlaybackHighlight(start, 20, playing: false); Layout(window);
            Assert.AreEqual(0, box.VerticalOffset, "Paused seeking must preserve manual reading.");
            editor.SetPlaybackHighlight(start, 20, playing: true); Layout(window);
            Assert.IsTrue(box.VerticalOffset > 0, "Play must center the current phrase even when the range has not changed.");
            box.ScrollToHome(); Layout(window); editor.ResumePlaybackFollow(); editor.SetPlaybackHighlight(start, 20); Layout(window);
            Assert.IsTrue(box.VerticalOffset > 0, "Explicit seeking during play must follow immediately.");
        }
        finally { window.Close(); }
    });
    [TestMethod]
    [DataRow(300, 1.0)]
    [DataRow(450, 1.5)]
    public Task WrappedPhraseIsCenteredAndEdgesStayWithinTheScrollRange(int width, double scale) => ScenarioNavigationTests.Sta(() => {
        using var editor = new MusicLyricsEditor(); var box = Box(editor);
        editor.LayoutTransform = new System.Windows.Media.ScaleTransform(scale, scale);
        var lines = Enumerable.Range(0, 80).Select(i => $"Строка {i}: кот зовёт солнце.").ToArray();
        lines[40] = "Сороковая строка: кот считает себя повелителем солнца, но однажды просыпает рассвет и придумывает забавное объяснение.";
        editor.Lyrics = string.Join("\n", lines);
        var window = new Window { Width = width, Height = 600, Content = editor, ShowInTaskbar = false };
        try {
            window.Show(); Layout(window); var original = editor.Lyrics; box.Select(2, 4); var undo = box.CanUndo;
            var start = original.IndexOf(lines[40], StringComparison.Ordinal);
            editor.SetPlaybackHighlight(start, lines[40].Length); Layout(window);
            var first = box.GetRectFromCharacterIndex(start); var last = box.GetRectFromCharacterIndex(start + lines[40].Length - 1, true);
            Assert.IsTrue(last.Bottom > first.Bottom, "The fixture must wrap to multiple display rows.");
            Assert.IsTrue(Math.Abs((first.Top + last.Bottom) / 2 - (box.Padding.Top + box.ViewportHeight / 2)) < first.Height,
                $"The whole wrapped phrase must be near the viewport center: first={first}; last={last}; viewport={box.ViewportHeight}; padding={box.Padding.Top}; offset={box.VerticalOffset}; extent={box.ExtentHeight}.");
            editor.SetPlaybackHighlight(0, lines[0].Length); Layout(window); Assert.AreEqual(0, box.VerticalOffset);
            var end = original.LastIndexOf(lines[^1], StringComparison.Ordinal);
            editor.SetPlaybackHighlight(end, lines[^1].Length); Layout(window);
            Assert.IsTrue(Math.Abs(box.VerticalOffset - (box.ExtentHeight - box.ViewportHeight)) < 1);
            Assert.AreEqual(original, editor.Lyrics); Assert.AreEqual(2, box.SelectionStart); Assert.AreEqual(4, box.SelectionLength);
            Assert.AreEqual(undo, box.CanUndo);
        }
        finally { window.Close(); }
    });
    private static System.Windows.Controls.TextBox Box(MusicLyricsEditor editor) => (System.Windows.Controls.TextBox)
        typeof(MusicLyricsEditor).GetField("_editor", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(editor)!;
    private static void Layout(Window window)
    {
        window.UpdateLayout(); var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame); window.UpdateLayout();
    }
}
