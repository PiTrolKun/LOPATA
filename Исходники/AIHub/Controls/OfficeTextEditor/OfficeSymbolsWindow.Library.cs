using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Services;
using AIHub.Services.OfficeEditor;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using FontFamily = System.Windows.Media.FontFamily;

namespace AIHub.Controls.OfficeTextEditor;

internal sealed partial class OfficeSymbolsWindow
{
    private readonly ComboBox _scope = new(), _block = new(), _font = new();
    private readonly TextBox _search = new() { MinHeight = 34 };
    private readonly TextBlock _count = new() { Margin = new(0, 8, 0, 6), TextWrapping = TextWrapping.Wrap };
    private readonly OfficeSymbolsBrowser _symbols = new();
    private readonly Grid _filters = new();
    private readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _filtering;
    private OfficeUnicodeCatalog? _catalog;
    private bool _closed;
    internal event Action? LibraryReady;

    private void BuildLibrary(DockPanel root)
    {
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = T("Search") }); top.Children.Add(_search);
        _search.ToolTip = T("SearchHint"); AutomationProperties.SetAutomationId(_search, "Office.Symbols.Search");
        _filters.Margin = new(0, 8, 0, 0);
        for (var i = 0; i < 3; i++) _filters.ColumnDefinitions.Add(new());
        void Filter(string label, ComboBox control, int column, string id)
        {
            var panel = new StackPanel { Margin = new(column == 0 ? 0 : 8, 0, 0, 0) };
            panel.Children.Add(new TextBlock { Text = T(label) }); panel.Children.Add(control);
            Grid.SetColumn(panel, column); _filters.Children.Add(panel);
            AutomationProperties.SetAutomationId(control, "Office.Symbols." + id);
        }
        Filter("Scope", _scope, 0, "Scope"); Filter("Block", _block, 1, "Block"); Filter("Font", _font, 2, "Font");
        top.Children.Add(_filters);
        top.Children.Add(new TextBlock { Text = T("FontHint"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 7, 0, 0) });
        top.Children.Add(_count); _count.Text = T("Loading");
        _scope.ItemsSource = Enum.GetNames<OfficeSymbolScope>().Select(T).ToArray(); _scope.SelectedIndex = 0;
        _font.ItemsSource = new[] { T("AutomaticFont") }.Concat(Fonts.SystemFontFamilies.Select(f => f.Source).Distinct().OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase)).ToArray();
        _font.SelectedIndex = 0;
        _filters.IsEnabled = false; _search.IsEnabled = false;
        AutomationProperties.SetAutomationId(_symbols, "Office.Symbols.Library");
        _symbols.Chosen += symbol =>
        {
            _selection.AppendText(symbol.Text); _selection.CaretIndex = _selection.Text.Length; _selection.Focus();
        };
        _symbols.Failed += error => { ComponentAuditFailures.Report(error); if (!_closed) _count.Text = T("DataMissing"); };
        root.Children.Add(_symbols);
        _search.TextChanged += (_, _) => ScheduleSearch();
        _scope.SelectionChanged += (_, _) => ScheduleSearch(); _block.SelectionChanged += (_, _) => ScheduleSearch();
        _font.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(new Action(SetGlyphFont));
        _font.LostKeyboardFocus += (_, _) => SetGlyphFont();
        _searchDelay.Tick += (_, _) => { _searchDelay.Stop(); _ = SearchAsync(); };
        Loaded += async (_, _) => await LoadLibraryAsync();
        Closed += (_, _) => { _closed = true; _searchDelay.Stop(); _lifetime.Cancel(); _symbols.CloseBrowser(); _filtering?.Cancel(); _filtering?.Dispose(); _lifetime.Dispose(); };
    }

    private void SetGlyphFont()
    {
        if (_closed || string.IsNullOrWhiteSpace(_font.Text)) return;
        var font = _font.Text == T("AutomaticFont") ? new FontFamily("Segoe UI Emoji, Segoe UI Symbol, Segoe UI") : new FontFamily(_font.Text);
        _selection.FontFamily = font;
        _symbols.SetStyle(_font.SelectedIndex <= 0 ? ["Segoe UI Emoji", "Segoe UI Symbol", "Segoe UI"]
                : [_font.Text, "Segoe UI Emoji", "Segoe UI Symbol", "Segoe UI"],
            ((SolidColorBrush)Resources["ReaderText"]).Color, ((SolidColorBrush)Resources["ReaderPanel"]).Color,
            ((SolidColorBrush)Resources["ReaderLine"]).Color, T("Selected"), _l("Navigation.Creation") == "Создание" ? "ru" : "en");
    }

    private async Task LoadLibraryAsync()
    {
        try
        {
            var token = _lifetime.Token;
            await ComponentLicenseGate.EnsureAsync(OfficeUnicodeCatalog.Component, token);
            _catalog = await OfficeUnicodeCatalog.LoadAsync(token);
            await _symbols.InitializeAsync(token);
            if (_closed) return;
            SetGlyphFont(); _symbols.SetSelection(_selection.Text);
            _block.ItemsSource = new[] { T("AllBlocks") }.Concat(_catalog.Blocks.Select(b => $"{b.Name} · U+{b.First:X4}–U+{b.Last:X4}")).ToArray();
            _block.SelectedIndex = 0; _searchDelay.Stop();
            _filters.IsEnabled = _search.IsEnabled = true;
            LibraryReady?.Invoke();
            await SearchAsync(); _search.Focus();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        { OwnedProcessRegistry.Log("office_symbols_failed", "Office.Symbols", detail: error.ToString());
            ComponentAuditFailures.Report(error); if (!_closed) { _count.Text = T("DataMissing"); _count.ToolTip = error.Message; } }
    }

    private void ScheduleSearch()
    {
        if (_catalog == null || _closed) return;
        _filtering?.Cancel(); _searchDelay.Stop(); _searchDelay.Start();
    }

    private async Task SearchAsync()
    {
        if (_catalog is not { } catalog || _closed) return;
        _filtering?.Cancel(); _filtering?.Dispose();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _filtering = cancellation;
        var token = cancellation.Token;
        var query = _search.Text; var scope = (OfficeSymbolScope)Math.Max(0, _scope.SelectedIndex); var block = _block.SelectedIndex - 1;
        try
        {
            var matches = await Task.Run(() => catalog.Search(query, scope, block, token), token);
            if (_closed || token.IsCancellationRequested) return;
            _symbols.SetItems(matches);
            _count.Text = string.Format(T("ResultCount"), matches.Length, catalog.Symbols.Length, catalog.Version, catalog.EmojiCount);
        }
        catch (OperationCanceledException) { }
        finally { if (ReferenceEquals(_filtering, cancellation)) _filtering = null; }
    }
}
