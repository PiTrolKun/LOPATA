using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Services.OfficeReader;
using Button = System.Windows.Controls.Button;
using ListBox = System.Windows.Controls.ListBox;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;
using ProgressBar = System.Windows.Controls.ProgressBar;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;

namespace AIHub.Controls.OfficeReader;

public sealed partial class ReaderWindow : Window
{
    private readonly Func<string, string> _l;
    private readonly Grid _root = new(), _body = new();
    private readonly ListBox _pages = new(), _contents = new();
    private readonly ScrollViewer _spreadScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly StackPanel _spread = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
    private readonly TextBlock _file = new(), _status = new(), _counter = new(), _zoomLabel = new();
    private readonly TextBox _pageNumber = new() { Width = 55 }, _search = new() { Width = 230 };
    private readonly WrapPanel _searchBar = new() { Visibility = Visibility.Collapsed };
    private readonly ProgressBar _progress = new() { Height = 3, IsIndeterminate = true, Visibility = Visibility.Collapsed };
    private readonly DispatcherTimer _resizeTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private Button _previous = null!, _next = null!, _bookButton = null!, _cancel = null!;
    private ReaderDocument? _document;
    private List<ReaderPageSlot> _slots = [];
    private CancellationTokenSource? _load;
    private int _page;
    private double _zoom = 100;
    private bool _book, _dark, _loading, _closed, _navigating, _fullscreen;
    private WindowState _previousState;
    private readonly Stack<int> _linkHistory = new();
    private readonly IAsyncDisposable _officeSession;
    private readonly ReaderWindowGroup _windowGroup;
    internal string? SourcePath { get; private set; }
    internal bool IsDarkTheme => _dark;

    public ReaderWindow(Window? owner, Func<string, string> localize, bool dark, string? path = null)
        : this(owner, localize, dark, path, ReaderWindowGroup.For(owner, localize, dark)) { }

    internal ReaderWindow(Window? owner, Func<string, string> localize, bool dark, string? path, ReaderWindowGroup group,
        bool startEditing = false, string? recoveryId = null)
    {
        _l = localize; _dark = dark; _windowGroup = group;
        SourcePath = path is null ? null : System.IO.Path.GetFullPath(path);
        if (owner is not null) { Owner = owner; Icon = owner.Icon; }
        Title = T("Title"); Width = 1240; Height = 850; MinWidth = 680; MinHeight = 420;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        Resources.MergedDictionaries.Add(new() { Source = new Uri("/AIHub;component/Controls/OfficeReader/ReaderResources.xaml", UriKind.Relative) });
        FontFamily = new FontFamily("Segoe UI"); FontSize = 14;
        AutomationProperties.SetAutomationId(this, "Office.Reader.Window");
        Build(); ApplyTheme();
        _shell.Children.Add(_root);
        Content = _shell;
        if (startEditing) MountEditor();
        _officeSession = ReaderOfficeSession.Retain();
        _windowGroup.Attach(this);
        _selection.CopyRequested = () => _ = CopySelectedTextAsync();
        _selection.SelectAllRequested = () => _selection.SelectAll(_document?.Pages.Count ?? 0);
        PreviewKeyDown += KeyPressed; PreviewMouseWheel += Wheel;
        SizeChanged += (_, _) => _resizeTimer.Start();
        _resizeTimer.Tick += (_, _) => { _resizeTimer.Stop(); ResizePages(true); };
        Closing += CheckEditorBeforeClosing;
        Closed += (_, _) => _cleanup = ReleaseWindowAsync();
        Loaded += async (_, _) =>
        {
            if (startEditing) await SwitchToEditingAsync(recoveryId);
            else if (path is not null) await OpenAsync(path);
        };
        UpdateNavigation(); _status.Text = T("Empty");
    }

    private Task _cleanup = Task.CompletedTask;
    private async Task ReleaseWindowAsync()
    {
        _closed = true; _resizeTimer.Stop(); CancelStatistics(); SavePosition(); _load?.Cancel();
        _pages.ItemsSource = null; _spread.Children.Clear();
        try
        {
            await CloseDocumentSpeechAsync();
            await CloseDocumentTranslationAsync(_document);
            if (_document is { } document) { _document = null; await document.DisposeAsync(); }
        }
        finally
        {
            _load?.Dispose();
            try { await _officeSession.DisposeAsync(); }
            finally { if (_editor is not null) await _editor.DisposeAsync(); }
        }
    }

    private string T(string key) => _l("Reader." + key);
    private Button Action(string caption, string key, Action action, string? tooltip = null)
    {
        var button = new Button { Content = caption, ToolTip = tooltip ?? caption };
        ToolTipService.SetShowOnDisabled(button, true);
        AutomationProperties.SetAutomationId(button, "Office.Reader." + key);
        AutomationProperties.SetName(button, tooltip ?? caption);
        button.Click += (_, _) => action(); return button;
    }

    private void Build()
    {
        _root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new());
        _root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel { Margin = new Thickness(14, 8, 14, 7) }; _root.Children.Add(header);
        var row = new WrapPanel(); header.Children.Add(row);
        row.Children.Add(Action(T("Open"), "Open", SelectFile));
        _enterEditor = Action(T("Editor"), "Editor", () => _ = SwitchToEditingAsync());
        row.Children.Add(_enterEditor);
        _saveReadingProject = Action(_l("OfficeEditor.CreateProject"), "CreateProject",
            () => _ = SaveReadingProjectAsync(), _l("OfficeEditor.ProjectRequired"));
        _saveReadingProject.Visibility = Visibility.Collapsed;
        row.Children.Add(_saveReadingProject);
        row.Children.Add(Action(T("Contents"), "Contents", ToggleContents));
        row.Children.Add(Action("↶", "Return", () => { if (_linkHistory.TryPop(out var page)) Go(page); }, T("Return")));
        row.Children.Add(Action(T("Find"), "Find", ToggleSearch));
        _bookButton = Action(T("Book"), "Book", ToggleBook); row.Children.Add(_bookButton);
        row.Children.Add(Action("−", "ZoomOut", () => SetZoom(_zoom - 10), T("ZoomOut")));
        _zoomLabel.VerticalAlignment = VerticalAlignment.Center; _zoomLabel.Margin = new Thickness(7, 0, 7, 0); row.Children.Add(_zoomLabel);
        row.Children.Add(Action("+", "ZoomIn", () => SetZoom(_zoom + 10), T("ZoomIn")));
        row.Children.Add(Action(T("Fit"), "Fit", Fit));
        row.Children.Add(Action("◐", "Theme", () => { _dark = !_dark; ApplyTheme(); SavePosition(); }, T("Theme")));
        row.Children.Add(Action("⛶", "Fullscreen", ToggleFullscreen, T("Fullscreen")));
        _speech = Action("🔊", "Speech", SpeechClicked, S("Title")); _speech.IsEnabled = false; row.Children.Add(_speech);
        var translation = new StackPanel();
        _translate = Action("文A", "Translation", TranslationClicked, Tr("Title")); translation.Children.Add(_translate);
        _summary = Action("≋", "Summary", SummaryClicked, _l("OfficeSummary.Title")); row.Children.Add(_summary);
        _translationToggle = Action("↔", "Translation.Toggle", ToggleTranslation, Tr("Toggle"));
        _translationToggle.Visibility = Visibility.Collapsed; _translationToggle.Padding = new(7, 0, 7, 0); translation.Children.Add(_translationToggle); row.Children.Add(translation);
        var ai = Action("AI", "AI", OpenAiSettings, T("Ai.Title")); ai.Margin = new Thickness(24, 3, 3, 3); row.Children.Add(ai);
        _cancel = Action(T("Cancel"), "Cancel", () => _load?.Cancel()); _cancel.Visibility = Visibility.Collapsed; row.Children.Add(_cancel);
        _file.Margin = new Thickness(5, 7, 0, 1); _file.TextTrimming = TextTrimming.CharacterEllipsis; header.Children.Add(_file);
        _searchBar.Children.Add(_search); _searchBar.Children.Add(Action(T("FindNext"), "FindNext", () => _ = FindAsync()));
        _searchBar.Children.Add(Action("×", "CloseSearch", ToggleSearch, T("CloseSearch"))); header.Children.Add(_searchBar);
        _search.KeyDown += (_, e) => { if (e.Key == Key.Enter) { _ = FindAsync(); e.Handled = true; } };
        Grid.SetRow(_progress, 1); _root.Children.Add(_progress);
        Grid.SetRow(_body, 2); _root.Children.Add(_body);
        _body.ColumnDefinitions.Add(new() { Width = new GridLength(0) }); _body.ColumnDefinitions.Add(new());
        _contents.Background = Brushes.Transparent; _contents.BorderThickness = new Thickness(0);
        _contents.Margin = new Thickness(10); _contents.DisplayMemberPath = nameof(ReaderHeading.Title);
        _contents.SelectionChanged += (_, _) => { if (_contents.SelectedItem is ReaderHeading heading) Go(heading.Page); };
        AutomationProperties.SetAutomationId(_contents, "Office.Reader.ContentsList"); _body.Children.Add(_contents);
        var canvas = new Grid(); Grid.SetColumn(canvas, 1); _body.Children.Add(canvas);
        _pages.Background = Brushes.Transparent; _pages.BorderThickness = new Thickness(0);
        _pages.ItemTemplate = (DataTemplate)Resources["ReaderPageTemplate"];
        _pages.ItemContainerStyle = (Style)Resources["ReaderListItem"];
        _pages.SetValue(VirtualizingPanel.IsVirtualizingProperty, true);
        _pages.SetValue(VirtualizingPanel.VirtualizationModeProperty, VirtualizationMode.Recycling);
        _pages.SetValue(VirtualizingPanel.ScrollUnitProperty, ScrollUnit.Pixel);
        _pages.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        _pages.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Scrolled));
        AutomationProperties.SetAutomationId(_pages, "Office.Reader.Pages"); canvas.Children.Add(_pages);
        _spreadScroll.Content = _spread; _spreadScroll.Visibility = Visibility.Collapsed; canvas.Children.Add(_spreadScroll);
        BuildFooter();
    }
}
