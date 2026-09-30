using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using Panel = System.Windows.Controls.Panel;
using TextBox = System.Windows.Controls.TextBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using UserControl = System.Windows.Controls.UserControl;

namespace AIHub.Controls;

/// <summary>Hosts existing settings controls once, without copying their state or handlers.</summary>
public sealed class SettingsWorkspace : UserControl
{
    private sealed record Section(string Id, string Icon, string TitleKey, string HelpKey, StackPanel Page, Button Navigation);
    private sealed record Target(SettingsSearchEntry Entry, FrameworkElement? Element, Action? Action);
    private readonly Grid _root = new(), _pages = new();
    private readonly StackPanel _navigation = new(), _results = new();
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly TextBox _search = new() { MinHeight = 40, Padding = new(12, 8, 12, 8) };
    private readonly TextBlock _searchLabel = new(), _sectionTitle = new(), _sectionHelp = new();
    private readonly TextBlock _searchPlaceholder = new() { IsHitTestVisible = false, Margin = new(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Dictionary<string, Section> _sections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Target> _targets = new(StringComparer.Ordinal);
    private Func<string, string> _text = key => key;
    private Action<string> _remember = _ => { };
    private Action _updates = () => { };
    public string SelectedSection { get; private set; } = "general";
    public TextBox SearchBox => _search;
    public int SearchResultCount { get; private set; }

    public SettingsWorkspace()
    {
        Content = _root;
        _root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new());
        _root.ColumnDefinitions.Add(new() { Width = new(224) });
        _root.ColumnDefinitions.Add(new() { Width = new(20) });
        _root.ColumnDefinitions.Add(new());
        var searchPanel = new StackPanel { Margin = new(0, 0, 0, 18), MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Stretch };
        _searchLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        _searchLabel.Margin = new(0, 0, 0, 6);
        var searchField = new Grid(); searchField.Children.Add(_search); searchField.Children.Add(_searchPlaceholder);
        _searchPlaceholder.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        searchPanel.Children.Add(searchField);
        searchPanel.MaxWidth = double.PositiveInfinity;
        Grid.SetColumnSpan(searchPanel, 3); _root.Children.Add(searchPanel);
        var navigationScroll = new ScrollViewer { Content = _navigation, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new(0, 0, 0, 10) };
        Grid.SetRow(navigationScroll, 1); _root.Children.Add(navigationScroll);
        var content = new Grid(); content.RowDefinitions.Add(new() { Height = GridLength.Auto }); content.RowDefinitions.Add(new());
        Grid.SetRow(content, 1); Grid.SetColumn(content, 2); _root.Children.Add(content);
        var heading = new StackPanel { Margin = new(0, 0, 0, 16) };
        _sectionTitle.SetResourceReference(TextBlock.FontSizeProperty, "UiCardTitleFontSize");
        _sectionTitle.FontWeight = FontWeights.SemiBold; _sectionTitle.TextWrapping = TextWrapping.Wrap;
        _sectionHelp.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        _sectionHelp.TextWrapping = TextWrapping.Wrap; _sectionHelp.Margin = new(0, 6, 0, 0);
        heading.Children.Add(_sectionTitle); heading.Children.Add(_sectionHelp); content.Children.Add(heading);
        _scroll.Content = _pages; Grid.SetRow(_scroll, 1); content.Children.Add(_scroll);
        _pages.Children.Add(_results);
        _search.TextChanged += (_, _) => { _searchPlaceholder.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Render(); };
        AutomationProperties.SetAutomationId(_search, "Settings.Search");
        SizeChanged += (_, _) =>
        {
            _root.ColumnDefinitions[0].Width = new(Math.Clamp(ActualWidth * .24, 180, 250));
            bool compact = ActualHeight < 350;
            _searchLabel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            _sectionHelp.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            searchPanel.Margin = new(0, 0, 0, compact ? 8 : 18);
            heading.Margin = new(0, 0, 0, compact ? 8 : 16);
        };
    }

    public void Configure(Func<string, string> text, Action<string> remember, Action updates)
    {
        _text = text; _remember = remember; _updates = updates;
        var updateButton = NavigationButton("updates");
        updateButton.Click += (_, _) => _updates();
        updateButton.Tag = "updates"; _navigation.Children.Add(updateButton);
        RefreshLocalization();
    }

    public void AddSection(string id, string icon, string titleKey, string helpKey, params FrameworkElement[] elements)
    {
        if (_sections.ContainsKey(id)) throw new InvalidOperationException("Duplicate settings section: " + id);
        var page = new StackPanel { Visibility = Visibility.Collapsed };
        foreach (var element in elements)
        {
            Detach(element);
            element.Visibility = Visibility.Visible;
            if (element is Border border)
            {
                border.Width = double.NaN; border.HorizontalAlignment = HorizontalAlignment.Stretch;
                border.CornerRadius = new(12); border.Margin = new(0, 0, 0, 14);
                border.SetResourceReference(Border.PaddingProperty, "UiPanelPadding");
            }
            page.Children.Add(element);
        }
        var button = NavigationButton(id); button.Tag = id; button.Click += (_, _) => SelectSection(id);
        _navigation.Children.Add(button); _pages.Children.Add(page);
        _sections.Add(id, new(id, icon, titleKey, helpKey, page, button));
    }

    public void AddTarget(SettingsSearchEntry entry, FrameworkElement? element = null, Action? action = null)
    {
        if (!_targets.TryAdd(entry.Id, new(entry, element, action))) throw new InvalidOperationException("Duplicate settings target: " + entry.Id);
    }

    public void RestoreSection(string? id) => SelectSection(id is not null && _sections.ContainsKey(id) ? id : "general", remember: false);

    public void SelectSection(string id, bool remember = true)
    {
        if (!_sections.ContainsKey(id)) return;
        if (_search.Text.Length > 0) _search.Clear();
        SelectedSection = id;
        if (remember) _remember(id);
        _scroll.ScrollToTop(); Render();
    }

    public void RefreshLocalization()
    {
        _searchLabel.Text = _text("Settings.Navigation.Search");
        _searchPlaceholder.Text = "⌕  " + _searchLabel.Text;
        _search.ToolTip = _searchLabel.Text;
        AutomationProperties.SetName(_search, _searchLabel.Text);
        foreach (Button button in _navigation.Children)
        {
            var id = (string)button.Tag;
            var title = id == "updates" ? _text("Settings.Navigation.updates.Title") : _text(_sections[id].TitleKey);
            var icon = id == "updates" ? "\uE895" : _sections[id].Icon;
            var label = new Grid(); label.ColumnDefinitions.Add(new() { Width = new(30) }); label.ColumnDefinitions.Add(new());
            label.Children.Add(new TextBlock { Text = icon, FontFamily = new("Segoe Fluent Icons, Segoe MDL2 Assets"), VerticalAlignment = VerticalAlignment.Center });
            var name = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(name, 1); label.Children.Add(name); button.Content = label;
            AutomationProperties.SetName(button, title);
        }
        Render();
    }

    private Button NavigationButton(string id)
    {
        var button = new Button { MinHeight = 46, Padding = new(12, 10, 12, 10), Margin = new(0, 0, 4, 5),
            HorizontalContentAlignment = HorizontalAlignment.Stretch };
        button.SetResourceReference(StyleProperty, "SettingsNavigationButtonStyle");
        AutomationProperties.SetAutomationId(button, "Settings.Section." + id); return button;
    }

    private void Render()
    {
        if (_sections.Count == 0) return;
        var searching = !string.IsNullOrWhiteSpace(_search.Text);
        _results.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        foreach (var section in _sections.Values)
        {
            section.Page.Visibility = !searching && section.Id == SelectedSection ? Visibility.Visible : Visibility.Collapsed;
            section.Navigation.SetResourceReference(BackgroundProperty,
                section.Id == SelectedSection && !searching ? "StepBadgeBrush" : "WindowBackgroundBrush");
        }
        if (!searching)
        {
            SearchResultCount = 0;
            var section = _sections[SelectedSection];
            _sectionTitle.Text = _text(section.TitleKey); _sectionHelp.Text = _text(section.HelpKey);
            _results.Children.Clear(); return;
        }
        _sectionTitle.Text = _text("Settings.Navigation.Results");
        _sectionHelp.Text = _text("Settings.Navigation.ResultsHelp");
        var entries = SettingsSearchIndex.Find(_targets.Values.Select(target => target.Entry), _search.Text, _text);
        SearchResultCount = entries.Count; _results.Children.Clear();
        if (entries.Count == 0) _results.Children.Add(new TextBlock { Text = _text("Settings.Navigation.NoResults"), TextWrapping = TextWrapping.Wrap });
        foreach (var entry in entries)
        {
            var result = new Button { HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new(0, 0, 0, 10) };
            result.SetResourceReference(StyleProperty, "SettingsActionButtonStyle");
            var label = new StackPanel();
            label.Children.Add(new TextBlock { Text = _text(entry.TitleKey), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            var help = new TextBlock { Text = _text(entry.SectionId == "updates" ? "Updates.Title" : _sections[entry.SectionId].TitleKey)
                + " · " + _text(entry.HelpKey), TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 0) };
            help.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush"); label.Children.Add(help);
            result.Content = label; AutomationProperties.SetAutomationId(result, "Settings.Result." + entry.Id);
            result.Click += (_, _) => OpenTarget(entry.Id); _results.Children.Add(result);
        }
    }

    public void OpenTarget(string id)
    {
        if (!_targets.TryGetValue(id, out var target)) return;
        if (target.Entry.SectionId == "updates") { _updates(); return; }
        SelectSection(target.Entry.SectionId);
        if (target.Element is { } element)
        {
            for (DependencyObject? parent = element; parent is not null; parent = LogicalTreeHelper.GetParent(parent))
                if (parent is Expander expander) expander.IsExpanded = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                element.BringIntoView();
                if (element.Focusable && element.IsEnabled) element.Focus();
            }));
        }
        target.Action?.Invoke();
    }

    private static void Detach(FrameworkElement element)
    {
        switch (element.Parent)
        {
            case Panel panel: panel.Children.Remove(element); break;
            case Decorator decorator: decorator.Child = null; break;
            case ContentControl content: content.Content = null; break;
            case null: break;
            default: throw new InvalidOperationException("Unsupported settings parent.");
        }
    }
}
