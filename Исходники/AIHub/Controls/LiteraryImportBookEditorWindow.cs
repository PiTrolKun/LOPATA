using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using AIHub.Services;
using AIHub.Services.LiteraryImport;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using ListBox = System.Windows.Controls.ListBox;
using MessageBox = System.Windows.MessageBox;

namespace AIHub.Controls;

/// <summary>One manuscript and one caret; source part files never leak into the reading workflow.</summary>
public sealed partial class LiteraryImportBookEditorWindow : Window
{
    private readonly string _root, _language;
    private readonly Func<string, string> _l;
    private readonly ImportReviewBookStore _store;
    private readonly ImportReviewBook _book;
    private readonly FileStream _lease;
    private readonly TextBox _text = new() { AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, UndoLimit = 200, FontSize = 18, Padding = new Thickness(22, 18, 22, 28) };
    private readonly TextBox _find = new() { Width = 150, Margin = new Thickness(4), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ListBox _contents = new() { DisplayMemberPath = "Title", BorderThickness = new Thickness(0) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 4) };
    private readonly ColumnDefinition _contentsColumn = new() { Width = new GridLength(220) };
    private readonly DispatcherTimer _save = new() { Interval = TimeSpan.FromMilliseconds(1200) };
    private readonly DispatcherTimer _position = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private LiteraryBookMarkAdorner? _marks;
    private AdornerLayer? _adornerLayer;
    private bool _dirty, _restoring = true, _navigating, _closed;
    private bool _docxPending;
    private sealed record ContentsItem(ReviewBookHeading Heading, string Title);
    public bool Changed { get; private set; }

    public LiteraryImportBookEditorWindow(Window owner, string root, string language, Func<string, string> localize)
    {
        _root = root; _language = language; _l = localize;
        _store = new(root); _lease = _store.AcquireEditor();
        try
        {
            _book = _store.Load();
            // Establish a stable, atomic manuscript before persisting positions against it.
            _store.Save(_book);
            LiteraryPromptDialogUi.Configure(this, owner, T("Title"));
            Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/AIHub;component/Controls/LiteraryBookScrollResources.xaml", UriKind.Relative)
            });
            Width = 1120; Height = 780; MinWidth = 680; MinHeight = 440;
            BuildUi();
            _text.Text = _book.Text;
            RefreshContents();
            LiterarySpellChecking.Enable(_text, language);
            ConnectEvents();
        }
        catch { _lease.Dispose(); throw; }
    }

    private string T(string key) => _l("Literary.BookReview." + key);
    private string Error(Exception ex) => ex.Message.StartsWith("Literary.", StringComparison.Ordinal) ? _l(ex.Message) : ex.Message;
    private Button Action(string key, Action action, bool primary = false)
        => LiteraryPromptDialogUi.Button(T(key), action, "BookReview." + key, primary);

    private void BuildUi()
    {
        var layout = new DockPanel { Margin = new Thickness(16) }; Content = layout;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); layout.Children.Add(top);
        top.Children.Add(LiteraryUi.Text(T("Hint")));
        var toolbar = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) }; top.Children.Add(toolbar);
        toolbar.Children.Add(Action("Contents", () => { _contentsColumn.Width = new GridLength(_contentsColumn.Width.Value == 0 ? 220 : 0); QueuePosition(); }));
        toolbar.Children.Add(Action("Undo", () => _text.Undo())); toolbar.Children.Add(Action("Redo", () => _text.Redo()));
        toolbar.Children.Add(Action("Cut", _text.Cut)); toolbar.Children.Add(Action("Copy", _text.Copy));
        toolbar.Children.Add(Action("Paste", _text.Paste));
        LiteraryPromptDialogUi.Identify(_find, "BookReview.Search");
        System.Windows.Automation.AutomationProperties.SetName(_find, T("Search"));
        toolbar.Children.Add(_find); toolbar.Children.Add(Action("Find", FindNext));
        toolbar.Children.Add(Action("Smaller", () => ResizeText(-1))); toolbar.Children.Add(Action("Larger", () => ResizeText(1)));
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);
        footer.Children.Add(_status);
        var buttons = new WrapPanel(); footer.Children.Add(buttons);
        buttons.Children.Add(Action("Save", () => SaveNow(true), true));
        buttons.Children.Add(Action("NextMark", NextMark));
        buttons.Children.Add(Action("Source", ShowSource));
        buttons.Children.Add(Action("ApproveMark", ApproveMark));
        buttons.Children.Add(Action("Close", Close));
        var main = new Grid(); layout.Children.Add(main);
        main.ColumnDefinitions.Add(_contentsColumn); main.ColumnDefinitions.Add(new());
        var contentsPanel = new DockPanel { Margin = new Thickness(0, 0, 12, 0), ClipToBounds = true };
        var heading = LiteraryUi.Text(T("Contents"), true); DockPanel.SetDock(heading, Dock.Top);
        contentsPanel.Children.Add(heading); contentsPanel.Children.Add(_contents); main.Children.Add(contentsPanel);
        LiteraryPromptDialogUi.Identify(_contents, "BookReview.Contents");
        var template = new DataTemplate();
        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Title"));
        label.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        label.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 7, 4, 7));
        template.VisualTree = label; _contents.DisplayMemberPath = ""; _contents.ItemTemplate = template;
        ScrollViewer.SetHorizontalScrollBarVisibility(_contents, ScrollBarVisibility.Disabled);
        var surface = new AdornerDecorator { Child = _text }; Grid.SetColumn(surface, 1); main.Children.Add(surface);
        LiteraryPromptDialogUi.Identify(_text, "BookReview.Manuscript");
        System.Windows.Automation.AutomationProperties.SetName(_text, T("Title"));
        _text.SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        _text.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
    }

    private void ConnectEvents()
    {
        _save.Tick += (_, _) => { _save.Stop(); SaveNow(); };
        _position.Tick += (_, _) => { _position.Stop(); SavePosition(); };
        _text.TextChanged += (_, e) =>
        {
            if (_restoring) return;
            var change = e.Changes.Count == 1 ? e.Changes.First() : null;
            _book.ReplaceText(_text.Text, change?.Offset, change?.RemovedLength ?? 0, change?.AddedLength ?? 0);
            _dirty = true; _docxPending = true;
            _status.Text = T("Saving"); _save.Stop(); _save.Start();
            _marks?.InvalidateVisual(); QueuePosition();
        };
        _text.SelectionChanged += (_, _) => QueuePosition();
        _text.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) =>
        { _marks?.InvalidateVisual(); QueuePosition(); }));
        _contents.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => QueuePosition()));
        _text.SizeChanged += (_, _) => _marks?.InvalidateVisual();
        _find.TextChanged += (_, _) => QueuePosition();
        _find.KeyDown += (_, e) => { if (e.Key == Key.Enter) { FindNext(); e.Handled = true; } };
        _contents.SelectionChanged += (_, _) =>
        {
            if (!_restoring && !_navigating && _contents.SelectedItem is ContentsItem item) Navigate(item.Heading.Start, 0);
        };
        PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F) { _find.Focus(); _find.SelectAll(); e.Handled = true; }
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S) { SaveNow(true); e.Handled = true; }
        };
        LocationChanged += (_, _) => QueuePosition(); SizeChanged += (_, _) => QueuePosition(); StateChanged += (_, _) => QueuePosition();
        Deactivated += (_, _) => { if (!_restoring && !_closed) { SaveNow(); SavePosition(); } };
        Loaded += (_, _) =>
        {
            _marks = new(_text, _book) { IsHitTestVisible = false };
            _adornerLayer = AdornerLayer.GetAdornerLayer(_text); _adornerLayer?.Add(_marks);
            RestorePosition(); UpdateStatus();
        };
        Closing += (_, e) =>
        {
            if (!SaveNow(true)) { e.Cancel = true; return; }
            SavePosition();
        };
        Closed += (_, _) => { _closed = true; _save.Stop(); _position.Stop(); if (_marks is not null) _adornerLayer?.Remove(_marks); _lease.Dispose(); };
    }

    private bool SaveNow(bool notify = false)
    {
        _save.Stop();
        try
        {
            if (_dirty) { _store.Save(_book); _dirty = false; Changed = true; RefreshContents(); }
            if (_docxPending || notify)
            {
                var folder = new LiteraryProjectLayout(_root).EnsureFolder("Exports/Import");
                ImportBookExporter.Export(_root, Path.Combine(folder, "book.docx"), _language == "ru", default);
                _docxPending = false;
            }
            UpdateStatus(); return true;
        }
        catch (Exception ex)
        {
            _status.Text = (_dirty ? T("SaveFailed") : T("ExportFailed")) + " " + Error(ex);
            if (notify) MessageBox.Show(this, _status.Text, T("Title"), MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void RefreshContents()
    {
        _navigating = true;
        var selected = (_contents.SelectedItem as ContentsItem)?.Heading.Id;
        _contents.ItemsSource = _book.Headings.Select((h, i) => new ContentsItem(h, $"{i + 1}. {_book.HeadingTitle(h)}")).ToArray();
        _contents.SelectedItem = _contents.Items.Cast<ContentsItem>().FirstOrDefault(i => i.Heading.Id == selected);
        _navigating = false;
    }
    private void UpdateStatus() => _status.Text = _dirty ? T("Saving") : string.Format(T("Saved"), _book.Marks.Count);
}
