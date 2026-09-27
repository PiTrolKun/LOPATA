using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AIHub.Services;
using Button = System.Windows.Controls.Button;

namespace AIHub.Controls;

public sealed partial class LiteraryDraftControl
{
    private string _viewedId = "";
    private readonly Dictionary<string, (int Caret, int FirstLine)> _readingPositions = [];
    private readonly Button _previousPart = new(), _nextPart = new();

    public string ViewedId => _viewedId;
    public bool IsStoreActiveViewed => !_loadFailed && _viewedId == Store.Index.ActiveId;
    public bool IsLatestViewed => !_loadFailed && _viewedId == OrderedParts().Last().Id;

    private LiteraryChapterPart[] OrderedParts() =>
        Store.Index.Parts.OrderBy(p => p.Chapter).ThenBy(p => p.Part).ToArray();

    private FrameworkElement BuildPartNavigation()
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _hint.Margin = new Thickness(0, 0, 8, 0);
        _hint.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(_hint);
        var arrows = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        foreach (var (button, label, step) in new[] { (_previousPart, "←", -1), (_nextPart, "→", 1) })
        {
            button.Content = label; button.Width = 36; button.Height = 30; button.MinWidth = 0;
            button.Padding = new Thickness(0); button.Margin = new Thickness(3, 0, 0, 0);
            button.FontSize = 18; button.SetResourceReference(StyleProperty, "SecondaryButtonStyle");
            button.Click += (_, _) => Navigate(step);
            arrows.Children.Add(button);
        }
        Grid.SetColumn(arrows, 1); row.Children.Add(arrows);
        RefreshNavigation();
        return row;
    }

    private void RefreshNavigation()
    {
        _previousPart.ToolTip = _l("Literary.Draft.PreviousPart");
        _nextPart.ToolTip = _l("Literary.Draft.NextPart");
        System.Windows.Automation.AutomationProperties.SetName(_previousPart, _l("Literary.Draft.PreviousPart"));
        System.Windows.Automation.AutomationProperties.SetName(_nextPart, _l("Literary.Draft.NextPart"));
        LiteraryChapterPart[] parts = _loadFailed ? [] : OrderedParts();
        var position = Array.FindIndex(parts, p => p.Id == _viewedId);
        _previousPart.Visibility = position > 0 ? Visibility.Visible : Visibility.Hidden;
        _nextPart.Visibility = position >= 0 && position < parts.Length - 1 ? Visibility.Visible : Visibility.Hidden;
        _previousPart.IsEnabled = _nextPart.IsEnabled = !_externalBlocked && !_busy && !_loadFailed;
    }

    public bool Navigate(int step)
    {
        if (_externalBlocked || _busy || _loadFailed) return false;
        var parts = OrderedParts();
        var position = Array.FindIndex(parts, p => p.Id == _viewedId);
        var target = position + step;
        if (position < 0 || target < 0 || target >= parts.Length) return false;
        return NavigateTo(parts[target].Id);
    }

    public bool NavigateToLast() => !_loadFailed && NavigateTo(OrderedParts().Last().Id);

    public bool IsSnapshotCurrent(LiteraryEditorSnapshot snapshot)
    {
        try
        {
            if (Store.Index.Transaction != snapshot.Transaction ||
                Store.LoadPart(snapshot.ActiveId) != snapshot.Text) return false;
            return _viewedId != snapshot.ActiveId || _editor.Text == snapshot.Text;
        }
        catch (Exception ex) when (IsStorageError(ex) || ex is InvalidOperationException) { return false; }
    }

    private bool NavigateTo(string id)
    {
        if (_externalBlocked || _busy || _loadFailed) return false;
        if (id == _viewedId) return Save();
        if (!Save()) { ShowMessage("Literary.Draft.SaveError"); return false; }
        var previous = _viewedId;
        try
        {
            // Read before changing the identity: a failed read leaves text and title together.
            var nextText = Store.LoadPart(id);
            var nextSaved = Store.PartLastSaved(id);
            var currentPosition = (_editor.CaretIndex, _editor.GetFirstVisibleLineIndex());
            _readingPositions[previous] = currentPosition;
            _restoring = true;
            _viewedId = id; _editor.Text = _accepted = _saved = nextText;
            _editor.CaretIndex = _readingPositions.TryGetValue(id, out var state) ? Math.Min(state.Caret, nextText.Length) : 0;
            _restoring = false;
            _lastSaved = nextSaved; _statusKey = "Literary.Draft.Saved";
            _discarded = false; _editor.IsUndoEnabled = false; _editor.IsUndoEnabled = true;
            if (_readingPositions.TryGetValue(id, out state) && state.FirstLine >= 0)
                Dispatcher.BeginInvoke(() => { if (_viewedId == id) _editor.ScrollToLine(state.FirstLine); }, DispatcherPriority.Loaded);
            Refresh(); RefreshNavigation(); ViewedChanged?.Invoke();
            return true;
        }
        catch (Exception ex) when (IsStorageError(ex) || ex is InvalidOperationException)
        {
            _restoring = false;
            ShowMessage("Literary.Draft.LoadError");
            return false;
        }
    }
}
