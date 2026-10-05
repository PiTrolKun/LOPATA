using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using AIHub.Services;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using ListBox = System.Windows.Controls.ListBox;
using Binding = System.Windows.Data.Binding;

namespace AIHub.Controls;

public sealed class MusicWishSelectionWindow : Window
{
    private readonly Func<string, string> _l;
    private readonly HashSet<string> _selected;
    private readonly List<Row> _rows = [];
    private readonly ListBox _list = new();
    private readonly TextBox _search = new(), _custom = new();
    private readonly CheckBox _onlySelected;
    private readonly TextBlock _count = new(), _empty = new();
    public string[]? AcceptedValues { get; private set; }
    public IReadOnlyCollection<string> SelectedValues => _selected.ToArray();
    public MusicWishSelectionWindow(Func<string, string> l, string category, IEnumerable<string> current,
        IReadOnlyDictionary<string, string[]>? recommendations = null)
    {
        _l = l; _selected = new(current, StringComparer.OrdinalIgnoreCase);
        MusicWishUi.PrepareWindow(this, l("Music.Wishes." + category), "Music.Selection." + category);
        var dock = new DockPanel { Margin = new(18) };
        var header = new StackPanel();
        header.Children.Add(MusicWishUi.Text(Title, true));
        header.Children.Add(MusicWishUi.Text(L(category == "genres" ? "GenreHint" : category == "instruments" ? "RecommendationsHint" : "WishHint")));
        header.Children.Add(MusicWishUi.Text(L("Search")));
        AutomationProperties.SetAutomationId(_search, "Music.Search"); _search.ToolTip = L("SearchHint"); header.Children.Add(_search);
        _onlySelected = MusicWishUi.Check(L("SelectedOnly"), false, _ => Filter()); header.Children.Add(_onlySelected);
        header.Children.Add(_count); DockPanel.SetDock(header, Dock.Top); dock.Children.Add(header);
        var bottom = new StackPanel(); bottom.Children.Add(MusicWishUi.Text(L("Custom")));
        var add = new DockPanel(); var addButton = MusicWishUi.Button(L("Add"), "Music.Custom.Add", AddCustom);
        DockPanel.SetDock(addButton, Dock.Right); add.Children.Add(addButton);
        AutomationProperties.SetAutomationId(_custom, "Music.Custom"); add.Children.Add(_custom); bottom.Children.Add(add);
        bottom.Children.Add(MusicWishUi.Footer(l, () => { AcceptedValues = _selected.Order(StringComparer.OrdinalIgnoreCase).ToArray(); DialogResult = true; }, () => DialogResult = false));
        DockPanel.SetDock(bottom, Dock.Bottom); dock.Children.Add(bottom);
        _empty.Text = L("NoResults"); DockPanel.SetDock(_empty, Dock.Top); dock.Children.Add(_empty);
        _list.SetResourceReference(BackgroundProperty, "PanelBrush"); _list.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        _list.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        _list.SetValue(VirtualizingPanel.IsVirtualizingProperty, true);
        _list.SetValue(VirtualizingPanel.VirtualizationModeProperty, VirtualizationMode.Recycling);
        var factory = new FrameworkElementFactory(typeof(CheckBox));
        factory.SetBinding(CheckBox.IsCheckedProperty, new Binding(nameof(Row.Selected)) { Mode = BindingMode.TwoWay });
        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetBinding(TextBlock.TextProperty, new Binding(nameof(Row.Label)));
        label.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(Row.Color)));
        label.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); factory.AppendChild(label);
        factory.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(Row.Hint)));
        factory.SetBinding(ForegroundProperty, new Binding(nameof(Row.Color)));
        factory.SetValue(FrameworkElement.MarginProperty, new Thickness(6));
        _list.ItemTemplate = new DataTemplate { VisualTree = factory };
        dock.Children.Add(_list); Content = dock;
        if (category == "genres")
            foreach (var genre in MusicWishCatalog.Genres) AddRow(genre.Name, MusicWishCatalog.GenreLabel(genre.Name, l), "");
        else
            foreach (var choice in MusicWishCatalog.Group(category))
            {
                var sources = recommendations?.GetValueOrDefault(choice.Id);
                var hint = sources is null ? "" : string.Format(L("Recommended"), string.Join(", ", sources));
                AddRow(choice.Id, (sources is null ? "" : "★ ") + MusicWishCatalog.Label(choice, l), hint);
            }
        foreach (var id in _selected.ToArray()) if (!_rows.Any(row => row.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) AddRow(id, id, "");
        _rows.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.CurrentCultureIgnoreCase));
        _search.TextChanged += (_, _) => Filter(); Filter();
    }
    private string L(string key) => _l("Music.Wishes." + key);
    private void AddRow(string id, string label, string hint) => _rows.Add(new Row(id, label, hint,
        _selected.Contains(id), this, value => { if (value) _selected.Add(id); else _selected.Remove(id); UpdateCount(); }));
    private void AddCustom()
    {
        var text = _custom.Text.Trim(); if (text.Length == 0) return;
        var row = _rows.FirstOrDefault(x => x.Id.Equals(text, StringComparison.OrdinalIgnoreCase) ||
            x.Label.Equals(text, StringComparison.CurrentCultureIgnoreCase));
        if (row is null) { AddRow(text, text, ""); row = _rows[^1]; }
        row.Selected = true; _custom.Clear(); _search.Clear(); Filter(); _list.ScrollIntoView(row);
    }
    private void Filter()
    {
        var visible = _rows.Where(row => (!_onlySelected.IsChecked.GetValueOrDefault() || row.Selected) &&
            (MusicWishCatalog.Matches(row.Label, _search.Text) || MusicWishCatalog.Matches(row.Id, _search.Text))).ToArray();
        _list.ItemsSource = visible; _empty.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed; UpdateCount();
    }
    private void UpdateCount() => _count.Text = string.Format(L("Selected"), _selected.Count);
    private sealed class Row(string id, string label, string hint, bool selected, FrameworkElement owner, Action<bool> changed) : INotifyPropertyChanged
    {
        private bool _selected = selected;
        public string Id { get; } = id;
        public string Label { get; } = label;
        public string Hint { get; } = hint;
        public object Color => owner.TryFindResource(string.IsNullOrEmpty(Hint) ? "TextPrimaryBrush" : "AccentBrush") ?? System.Windows.Media.Brushes.Gray;
        public bool Selected { get => _selected; set { if (_selected == value) return; _selected = value; changed(value); PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
