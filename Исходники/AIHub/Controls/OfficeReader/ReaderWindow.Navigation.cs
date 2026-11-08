using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Services.OfficeReader;
using TextBox = System.Windows.Controls.TextBox;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Brush = System.Windows.Media.Brush;

namespace AIHub.Controls.OfficeReader;

public sealed partial class ReaderWindow
{
    private void UpdateNavigation()
    {
        var count = _document?.Pages.Count ?? 0;
        _pageNumber.Text = count > 0 ? (_page + 1).ToString() : "0";
        _pageNumber.IsEnabled = count > 0 && !_loading;
        _counter.Text = "/ " + count;
        _previous.IsEnabled = count > 0 && _page > 0 && !_loading;
        _next.IsEnabled = count > 0 && (_book ? ReaderNavigation.SpreadStart(_page) + 2 : _page + 1) < count && !_loading;
        _zoomLabel.Text = _zoom.ToString("0") + "%";
        _bookButton.Content = T(_book ? "Vertical" : "Book");
        _speech.IsEnabled = count > 0 && !_loading && !_translating;
        _translate.IsEnabled = count > 0 && !_loading && !_speaking;
        _summary.IsEnabled = count > 0 && !_loading && !_speaking && !_translating;
        _translationToggle.IsEnabled = !_loading && !_translating && !_speaking;
        UpdateReaderProjectOffer();
    }

    private void Go(int page)
    {
        if (_document is null || _slots.Count == 0) return;
        _page = ReaderNavigation.ClampPage(page, _slots.Count);
        if (_book)
        {
            _page = ReaderNavigation.SpreadStart(_page); _spread.Children.Clear();
            foreach (var slot in _slots.Skip(_page).Take(2)) _spread.Children.Add(new ReaderPageView { DataContext = slot });
            _spreadScroll.ScrollToHorizontalOffset(0); _spreadScroll.ScrollToVerticalOffset(0);
        }
        else
        {
            _navigating = true;
            _pages.ScrollIntoView(_slots[_page]);
            Dispatcher.BeginInvoke(() =>
            {
                if (_book || _document is null) { _navigating = false; return; }
                var scroll = FindScroll(_pages);
                scroll?.ScrollToVerticalOffset(_slots.Take(_page).Sum(s => s.Height + 24));
                _navigating = false;
            }, DispatcherPriority.Loaded);
        }
        UpdateNavigation(); SavePosition();
    }

    private void Move(int direction)
    { if (_document is not null) Go(ReaderNavigation.Next(_page, _slots.Count, _book, direction)); }

    private void Scrolled(object sender, ScrollChangedEventArgs e)
    {
        if (_navigating || _book || _slots.Count == 0 || e.VerticalChange == 0) return;
        var offset = e.VerticalOffset; var page = 0;
        while (page + 1 < _slots.Count && offset >= _slots[page].Height + 24)
        { offset -= _slots[page].Height + 24; page++; }
        if (_page != page) { _page = page; UpdateNavigation(); SavePosition(); }
    }

    private void ToggleBook()
    {
        _book = !_book; ShowMode();
        if (_book) Fit();
        ResizePages(true); Go(_page); UpdateNavigation(); SavePosition();
    }

    private void ShowMode()
    {
        _pages.ItemsSource = _book ? null : _slots; _spread.Children.Clear();
        _pages.Visibility = _book ? Visibility.Collapsed : Visibility.Visible;
        _spreadScroll.Visibility = _book ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetZoom(double value)
    { _zoom = ReaderNavigation.ClampZoom(value); ResizePages(true); Go(_page); UpdateNavigation(); SavePosition(); }

    private void ResizePages(bool render)
    {
        // 100% uses the document's page dimensions in device-independent pixels.
        foreach (var slot in _slots) slot.Resize(slot.Document.Pages[slot.Index].Width * _zoom / 100, render);
    }

    private void Fit()
    {
        if (_document is null) return;
        var width = _body.ActualWidth - _body.ColumnDefinitions[0].ActualWidth - (_book ? 64 : 46);
        var page = _document.Pages[_page];
        if (_book)
        {
            var spread = _document.Pages.Skip(ReaderNavigation.SpreadStart(_page)).Take(2).ToArray();
            SetZoom(Math.Min(width / spread.Sum(p => p.Width), (_body.ActualHeight - 40) / spread.Max(p => p.Height)) * 100);
        }
        else SetZoom(width / page.Width * 100);
    }

    private void ToggleContents()
    { _body.ColumnDefinitions[0].Width = new GridLength(_body.ColumnDefinitions[0].Width.Value == 0 ? 230 : 0); }

    private void ToggleSearch()
    {
        _searchBar.Visibility = _searchBar.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (_searchBar.Visibility == Visibility.Visible) _search.Focus();
    }

    private void ToggleFullscreen()
    {
        if (!_fullscreen)
        { _previousState = WindowState; WindowState = WindowState.Normal; WindowStyle = WindowStyle.None; WindowState = WindowState.Maximized; }
        else { WindowState = WindowState.Normal; WindowStyle = WindowStyle.SingleBorderWindow; WindowState = _previousState; }
        _fullscreen = !_fullscreen;
        UpdateFooterFont();
    }

    private void Wheel(object sender, MouseWheelEventArgs e)
    {
        if (IsEditing) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { SetZoom(_zoom + Math.Sign(e.Delta) * 10); e.Handled = true; }
        else if (_book && _spreadScroll.ScrollableHeight < 1 && _spreadScroll.ScrollableWidth < 1)
        { Move(e.Delta < 0 ? 1 : -1); e.Handled = true; }
    }

    private void KeyPressed(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (IsEditing) return;
        var control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (e.Key == Key.F11) ToggleFullscreen();
        else if (e.Key == Key.Escape && _fullscreen) ToggleFullscreen();
        else if (e.Key == Key.Escape && _searchBar.IsVisible) ToggleSearch();
        else if (control && e.Key == Key.O) SelectFile();
        else if (control && e.Key == Key.F) { _searchBar.Visibility = Visibility.Visible; _search.Focus(); }
        else if (e.OriginalSource is TextBox) return;
        else if (control && e.Key == Key.C) _ = CopySelectedTextAsync();
        else if (control && e.Key == Key.A) _selection.SelectAll(_document?.Pages.Count ?? 0);
        else if (e.Key == Key.PageDown || (_book && e.Key == Key.Right)) Move(1);
        else if (e.Key == Key.PageUp || (_book && e.Key == Key.Left)) Move(-1);
        else if (control && e.Key == Key.Home) Go(0);
        else if (control && e.Key == Key.End) Go(_slots.Count - 1);
        else return;
        e.Handled = true;
    }

    private static ScrollViewer? FindScroll(DependencyObject root)
    {
        if (root is ScrollViewer scroll) return scroll;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScroll(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    public void ApplyTheme(bool dark) { _dark = dark; ApplyTheme(); _editor?.SetTheme(dark); }
    private void ApplyTheme()
    {
        SolidColorBrush Brush(string value) => new((Color)ColorConverter.ConvertFromString(value));
        Resources["ReaderText"] = Brush(_dark ? "#F1F4FA" : "#202735");
        Resources["ReaderPanel"] = Brush(_dark ? "#172132" : "#FFFFFF");
        Resources["ReaderLine"] = Brush(_dark ? "#3A465C" : "#CCD3DF");
        Resources["ReaderHover"] = Brush(_dark ? "#293954" : "#E7EFFF");
        Background = Brush(_dark ? "#0D1523" : "#EDF0F6"); Foreground = (Brush)Resources["ReaderText"];
        _contents.Foreground = Foreground; _status.Foreground = Brush(_dark ? "#B3BED0" : "#526077");
        Resources["WindowBackgroundBrush"] = Background; Resources["TextPrimaryBrush"] = Foreground;
        Resources["TextSecondaryBrush"] = _status.Foreground;
        Resources["PanelBrush"] = Resources["InputBrush"] = Resources["SecondaryButtonBackgroundBrush"] = Resources["ReaderPanel"];
        Resources["LineBrush"] = Resources["ReaderLine"]; Resources["AccentBrush"] = Brush("#2563EB"); Resources["UiBodyFontSize"] = 14d;
    }
}
