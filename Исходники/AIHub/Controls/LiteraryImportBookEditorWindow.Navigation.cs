using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AIHub.Services;
using AIHub.Services.LiteraryImport;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls;

public sealed partial class LiteraryImportBookEditorWindow
{
    private void Navigate(int start, int length)
    {
        start = Math.Clamp(start, 0, _text.Text.Length);
        _text.Focus(); _text.Select(start, Math.Clamp(length, 0, _text.Text.Length - start));
        _text.UpdateLayout(); _text.ScrollToLine(Math.Max(0, _text.GetLineIndexFromCharacterIndex(start)));
        QueuePosition();
    }
    private void FindNext()
    {
        if (_find.Text.Length == 0) return;
        var start = Math.Min(_text.SelectionStart + _text.SelectionLength, _text.Text.Length);
        var found = _text.Text.IndexOf(_find.Text, start, StringComparison.CurrentCultureIgnoreCase);
        if (found < 0) found = _text.Text.IndexOf(_find.Text, StringComparison.CurrentCultureIgnoreCase);
        if (found < 0) { _status.Text = T("NotFound"); return; }
        Navigate(found, _find.Text.Length);
    }
    private ReviewBookMark? CurrentMark() => _book.Marks.FirstOrDefault(m =>
        m.Start <= _text.SelectionStart && m.Start + m.Length > _text.SelectionStart);
    private void NextMark()
    {
        var from = _text.SelectionStart + Math.Max(1, _text.SelectionLength);
        var mark = _book.Marks.OrderBy(m => m.Start).FirstOrDefault(m => m.Start >= from)
            ?? _book.Marks.OrderBy(m => m.Start).FirstOrDefault();
        if (mark is null) { _status.Text = T("NoMarks"); return; }
        Navigate(mark.Start, mark.Length);
        _status.Text = mark.Reason.StartsWith("Literary.", StringComparison.Ordinal) ? _l(mark.Reason) : mark.Reason;
    }
    private void ApproveMark()
    {
        var mark = CurrentMark();
        if (mark is null) { _status.Text = T("SelectMark"); return; }
        _book.Marks.Remove(mark); _dirty = true; _docxPending = true;
        _marks?.InvalidateVisual(); SaveNow(true);
    }
    private void ShowSource()
    {
        var mark = CurrentMark();
        if (mark is null) { _status.Text = T("SelectMark"); return; }
        try
        {
            var units = JsonSerializer.Deserialize<ImportUnit[]>(LiteraryChapterFiles.Read(
                Path.Combine(_root, "Import", "sources.json")), ImportJson.Options)!;
            var source = units.FirstOrDefault(u => u.Id == mark.UnitId)?.Text ?? T("SourceUnavailable");
            var window = new Window { Width = 850, Height = 650 };
            LiteraryPromptDialogUi.Configure(window, this, T("Source"));
            var text = new TextBox { Text = mark.Reason + "\n\n" + source, IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(16), FontSize = 18 };
            text.SetResourceReference(BackgroundProperty, "WindowBackgroundBrush"); text.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            window.Content = text; window.ShowDialog();
        }
        catch (Exception ex) { _status.Text = Error(ex); }
    }
    private void ResizeText(int delta)
    {
        var first = _text.GetFirstVisibleLineIndex();
        var at = first < 0 ? 0 : _text.GetCharacterIndexFromLineIndex(first);
        _text.FontSize = Math.Clamp(_text.FontSize + delta, 12, 32);
        _text.UpdateLayout(); _text.ScrollToLine(Math.Max(0, _text.GetLineIndexFromCharacterIndex(at)));
        QueuePosition();
    }

    private void QueuePosition()
    {
        if (_restoring || _closed) return;
        _position.Stop(); _position.Start();
    }
    private static ScrollViewer? FindScroll(DependencyObject element)
    {
        if (element is ScrollViewer scroll) return scroll;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(element); i++)
            if (FindScroll(System.Windows.Media.VisualTreeHelper.GetChild(element, i)) is { } child) return child;
        return null;
    }
    private void SavePosition()
    {
        if (_restoring || _closed) return;
        _position.Stop();
        // Persist text first so an interrupted session never stores a caret against unsaved text.
        if (_dirty && !SaveNow()) return;
        try
        {
            var first = _text.GetFirstVisibleLineIndex();
            var character = first < 0 ? 0 : _text.GetCharacterIndexFromLineIndex(first);
            var rect = _text.GetRectFromCharacterIndex(Math.Clamp(character, 0, _text.Text.Length));
            var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
            new ImportBookEditorState
            {
                Revision = LiteraryWorkIndex.Revision(_book.Text), SelectionStart = _text.SelectionStart,
                SelectionLength = _text.SelectionLength, TopCharacter = character, TopPixel = rect.IsEmpty ? 0 : rect.Top,
                VerticalOffset = _text.VerticalOffset, Search = _find.Text, FontSize = _text.FontSize,
                Left = bounds.Left, Top = bounds.Top, Width = bounds.Width, Height = bounds.Height,
                Maximized = WindowState == WindowState.Maximized, ContentsVisible = _contentsColumn.Width.Value != 0,
                ContentsId = (_contents.SelectedItem as ContentsItem)?.Heading.Id ?? "", ContentsOffset = FindScroll(_contents)?.VerticalOffset ?? 0
            }.Save(_root);
        }
        catch (Exception ex) { _status.Text = T("PositionFailed") + " " + Error(ex); }
    }
    private void RestorePosition()
    {
        var state = ImportBookEditorState.Load(_root)?.Clamp(_text.Text.Length);
        if (state is not null)
        {
            Width = state.Width; Height = state.Height;
            // Monitor coordinates are physical pixels; saved WPF coordinates are DIPs.
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            var visible = double.IsFinite(state.Left) && double.IsFinite(state.Top)
                && System.Windows.Forms.Screen.AllScreens.Any(screen =>
                {
                    var area = screen.WorkingArea;
                    return new Rect(area.Left / dpi.DpiScaleX, area.Top / dpi.DpiScaleY,
                        area.Width / dpi.DpiScaleX, area.Height / dpi.DpiScaleY)
                        .IntersectsWith(new Rect(state.Left, state.Top, Math.Min(100, state.Width), 40));
                });
            if (visible) { WindowStartupLocation = WindowStartupLocation.Manual; Left = state.Left; Top = state.Top; }
            _text.FontSize = state.FontSize; _find.Text = state.Search;
            _contentsColumn.Width = new GridLength(state.ContentsVisible ? 220 : 0);
            if (state.Maximized) WindowState = WindowState.Maximized;
            _text.Focus(); _text.Select(state.SelectionStart, state.SelectionLength);
        }
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (_closed) return;
            _text.UpdateLayout();
            if (state is not null)
            {
                _contents.SelectedItem = _contents.Items.Cast<ContentsItem>().FirstOrDefault(i => i.Heading.Id == state.ContentsId);
                FindScroll(_contents)?.ScrollToVerticalOffset(state.ContentsOffset);
                var line = _text.GetLineIndexFromCharacterIndex(state.TopCharacter);
                _text.ScrollToLine(Math.Max(0, line)); _text.UpdateLayout();
                var rect = _text.GetRectFromCharacterIndex(state.TopCharacter);
                if (!rect.IsEmpty) _text.ScrollToVerticalOffset(Math.Max(0, _text.VerticalOffset + rect.Top - state.TopPixel));
                else _text.ScrollToVerticalOffset(state.VerticalOffset);
            }
            _restoring = false; _marks?.InvalidateVisual();
        }));
    }
}
