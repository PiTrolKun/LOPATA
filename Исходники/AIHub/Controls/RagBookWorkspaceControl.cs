using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services;
using AIHub.Models;
using UserControl = System.Windows.Controls.UserControl;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using ListBox = System.Windows.Controls.ListBox;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace AIHub.Controls;

/// <summary>Embedded book preparation and acquisition with durable background execution.</summary>
public sealed partial class RagBookWorkspaceControl : UserControl, IDisposable
{
    private readonly Func<string, string> _l;
    private readonly Dictionary<string, string> _labels = [];
    private readonly RagBookDraftStore _store;
    private readonly string _modelsRoot;
    private int _connections;
    private readonly Action<string> _openSource;
    private readonly Action _showLicenses;
    private readonly List<string> _books = [];
    private readonly ListBox _list = new() { MinHeight = 150, MaxHeight = 400 };
    private readonly TextBox _name = new(), _output = new() { IsReadOnly = true };
    private readonly System.Windows.Controls.ComboBox _cpuTarget = new() { Width = 110, HorizontalAlignment = HorizontalAlignment.Left,
        ItemsSource = new[] { 25, 50, 75, 90 }, SelectedItem = 50, Margin = new Thickness(0, 4, 0, 6) };
    private readonly TextBox _preview = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 220, MaxHeight = 420 };
    private readonly TextBlock _source = new(), _status = new(), _count = new();
    private readonly Button _remove = new(), _view = new();
    private readonly List<Button> _sourceActions = [];
    private bool _canSave = true;
    private int _previewVersion;
    private CancellationTokenSource? _previewCancellation;

    public RagBookWorkspaceControl(Func<string, string> localize, string modelsRoot,
        string resultsRoot, int connections, Action<string> openSource, Action showLicenses, RagBookDraftStore? store = null)
    {
        _l = localize; _modelsRoot = modelsRoot; _connections = connections;
        _openSource = openSource; _showLicenses = showLicenses;
        _store = store ?? new(Path.Combine(AppDataPaths.BaseDirectory, "BookPreparation", "workspace.json"));
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/RagBookResources.xaml", UriKind.Relative) });
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        SetResourceReference(FontSizeProperty, "UiBodyFontSize");
        AutomationProperties.SetAutomationId(this, "RagBooks.Page");
        Build();
        try
        {
            var draft = _store.Load();
            _name.Text = string.IsNullOrWhiteSpace(draft.Name) ? T("DefaultName") : draft.Name;
            _output.Text = string.IsNullOrWhiteSpace(draft.OutputDirectory)
                ? Path.Combine(resultsRoot, "KnowledgeLibraries") : draft.OutputDirectory;
            _books.AddRange(draft.Books.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase));
            _cpuTarget.SelectedItem = new[] { 25, 50, 75, 90 }.Contains(draft.CpuTargetPercent) ? draft.CpuTargetPercent : 50;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _canSave = false; _name.Text = T("DefaultName"); _output.Text = Path.Combine(resultsRoot, "KnowledgeLibraries");
            _status.Text = T("DraftReadError");
        }
        RefreshBooks();
        InitializeProcessing();
        _name.LostKeyboardFocus += (_, _) => SaveDraft();
        _cpuTarget.SelectionChanged += (_, _) => SaveDraft();
        Unloaded += (_, _) => SaveDraft();
    }

    private string T(string key) { var value = _l("RagBooks." + key); _labels.TryAdd(key, value); return value; }
    public void RefreshLocalization()
    {
        RagBookUiLocalization.Refresh(this, _labels, _l); _components?.RefreshLocalization();
        if (_searchLayer.Items.Count == 2)
        {
            ((ComboBoxItem)_searchLayer.Items[0]).Content = T("SearchBooks");
            ((ComboBoxItem)_searchLayer.Items[1]).Content = T("SearchLexicon");
            AutomationProperties.SetName(_searchLayer, T("SearchLayer"));
        }
        _count.Text = string.Format(T("BookCount"), _books.Count);
        RefreshProcessingState(); ShowLibrary();
        if (_books.Count == 0) { _source.Text = T("NoBooks"); _preview.Text = T("NoBooks"); }
        else _ = ShowBookAsync();
    }
    public void ConfigureConnections(int connections) { _connections = connections; _components?.ConfigureConnections(connections); }

    private void Build()
    {
        var root = new DockPanel { Margin = new Thickness(24) };
        var heading = new StackPanel(); DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        heading.Children.Add(Text(T("Title"), true));
        heading.Children.Add(Text(T("Hint")));
        var tools = new WrapPanel { Margin = new Thickness(0, 12, 0, 16) };
        var add = ActionButton(T("ChooseBooks"), ChooseBooks, "RagBooks.Add", true); _sourceActions.Add(add); tools.Children.Add(add);
        AddLexiconAction(tools);
        tools.Children.Add(ActionButton(T("Components"), OpenComponents, "RagBooks.Components"));
        tools.Children.Add(ActionButton(T("Licenses"), _showLicenses, "RagBooks.Licenses"));
        heading.Children.Add(tools);

        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        _status.Text = T("ProcessingHint"); _status.TextWrapping = TextWrapping.Wrap;
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush"); footer.Children.Add(_status);
        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        ConfigureButton(_start, T("Start"), () => _ = StartProcessingAsync(), "RagBooks.Start"); actions.Children.Add(_start);
        ConfigureButton(_pauseProcessing, T("Pause"), () => _ = PauseProcessingAsync(), "RagBooks.Pause"); actions.Children.Add(_pauseProcessing);
        ConfigureButton(_cancelProcessing, T("Cancel"), () => _ = CancelProcessingAsync(), "RagBooks.Cancel"); actions.Children.Add(_cancelProcessing);
        actions.Children.Add(ActionButton(T("Back"), () => BackRequested?.Invoke(), "RagBooks.Back")); footer.Children.Add(actions);

        var grid = new Grid(); grid.ColumnDefinitions.Add(new() { Width = new GridLength(300) });
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var books = BuildBookList(); var workspace = BuildWorkspace();
        grid.Children.Add(books); grid.Children.Add(workspace); Grid.SetColumn(workspace, 1);
        grid.SizeChanged += (_, _) =>
        {
            var narrow = grid.ActualWidth < 880;
            grid.ColumnDefinitions[0].Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(300);
            grid.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(workspace, narrow ? 0 : 1); Grid.SetRow(workspace, narrow ? 1 : 0);
            workspace.Margin = narrow ? new Thickness(0, 12, 0, 0) : new Thickness(16, 0, 0, 0);
        };
        root.Children.Add(new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        _workspace = root; _host.Children.Add(root); Content = _host;
    }

    private Border BuildBookList()
    {
        var panel = new StackPanel(); panel.Children.Add(Text(T("Books"), true));
        panel.Children.Add(Text(T("Formats"))); panel.Children.Add(_count);
        _list.Margin = new Thickness(0, 10, 0, 10);
        _list.SelectedValuePath = nameof(BookItem.Path);
        AutomationProperties.SetAutomationId(_list, "RagBooks.Books");
        _list.SelectionChanged += async (_, _) => await ShowBookAsync();
        panel.Children.Add(_list);
        var actions = new WrapPanel();
        ConfigureButton(_remove, T("Remove"), RemoveBook, "RagBooks.Remove");
        ConfigureButton(_view, T("OpenSource"), () => { if (_list.SelectedValue is string path) _openSource(path); }, "RagBooks.OpenSource");
        actions.Children.Add(_remove); actions.Children.Add(_view); panel.Children.Add(actions);
        _source.TextWrapping = TextWrapping.Wrap; _source.Margin = new Thickness(0, 12, 0, 0); panel.Children.Add(_source);
        return Surface(panel);
    }

    private Border BuildWorkspace()
    {
        var panel = new StackPanel(); panel.Children.Add(Text(T("Library"), true));
        panel.Children.Add(Text(T("LibraryName"))); _name.Margin = new Thickness(0, 4, 0, 10);
        AutomationProperties.SetAutomationId(_name, "RagBooks.LibraryName"); panel.Children.Add(_name);
        panel.Children.Add(Text(T("OutputFolder")));
        AutomationProperties.SetAutomationId(_output, "RagBooks.OutputFolder"); panel.Children.Add(_output);
        var folder = ActionButton(T("ChooseFolder"), ChooseFolder, "RagBooks.ChooseFolder"); _sourceActions.Add(folder); panel.Children.Add(folder);
        panel.Children.Add(Text(T("CpuTarget")));
        AutomationProperties.SetAutomationId(_cpuTarget, "RagBooks.CpuTarget"); panel.Children.Add(_cpuTarget);
        panel.Children.Add(Text(T("CpuTargetHint")));
        panel.Children.Add(Text(T("Stages"), true));
        var stages = new WrapPanel { Margin = new Thickness(0, 4, 0, 10) };
        foreach (var key in new[] { "Extract", "Clean", "Split", "Index", "Validate" })
        {
            var label = Text(T("Stage." + key)); label.Margin = new Thickness(0, 0, 12, 6);
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush"); stages.Children.Add(label);
        }
        panel.Children.Add(stages);
        AutomationProperties.SetAutomationId(_processingProgress, "RagBooks.Progress"); panel.Children.Add(_processingProgress);
        var tabs = _processingTabs;
        tabs.Items.Add(new TabItem { Header = T("Preview"), Content = _preview });
        tabs.Items.Add(new TabItem { Header = T("Journal"), Content = _journal });
        var result = new StackPanel { Margin = new Thickness(12) };
        result.Children.Add(_resultDescription); result.Children.Add(Text(T("TrySearch"), true));
        BuildSearchLayer(result);
        AutomationProperties.SetAutomationId(_query, "RagBooks.Query"); result.Children.Add(_query);
        ConfigureButton(_find, T("Find"), () => _ = FindAsync(), "RagBooks.Find"); result.Children.Add(_find); result.Children.Add(_hits);
        AutomationProperties.SetAutomationId(_hits, "RagBooks.Hits");
        tabs.Items.Add(new TabItem { Header = T("Result"), Content = result });
        AutomationProperties.SetAutomationId(_preview, "RagBooks.Preview"); panel.Children.Add(tabs);
        return Surface(panel);
    }

    private readonly Grid _host = new();
    private FrameworkElement? _workspace;
    private RagBookComponentsControl? _components;
    public event Action? BackRequested;

    private RagBookComponentsControl EnsureComponents(string? root = null, int? connections = null)
    {
        root ??= _modelsRoot;
        if (_components is not null && !string.Equals(_components.ModelsRoot, root, StringComparison.OrdinalIgnoreCase))
        {
            if (_components.IsWorking) throw new InvalidOperationException(T("OtherOperation"));
            _host.Children.Remove(_components); _components.Dispose(); _components = null;
        }
        if (_components is null)
        {
            _components = new(_l, root, connections ?? _connections, _showLicenses) { Visibility = Visibility.Collapsed };
            _components.BackRequested += () => GoBack();
            _host.Children.Add(_components);
        }
        return _components;
    }
    public void OpenComponents()
    {
        var components = _components ?? EnsureComponents();
        _workspace!.Visibility = Visibility.Collapsed; components.Visibility = Visibility.Visible;
    }
    public bool GoBack()
    {
        SaveDraft();
        if (_components is not { Visibility: Visibility.Visible }) return false;
        _components.Visibility = Visibility.Collapsed; _workspace!.Visibility = Visibility.Visible; return true;
    }
    public Task ResumeAcquisitionAsync(BackgroundOperationState state, CancellationToken token)
    {
        var input = RagBookAcquisitionWork.Read(state);
        return EnsureComponents(input.ModelsRoot, _connections).ResumeAsync(state, token);
    }
    public void Dispose() { DisposeProcessing(); ++_previewVersion; _previewCancellation?.Cancel(); _components?.Dispose(); }

    private void ChooseBooks()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, CheckFileExists = true,
            Title = T("ChooseBooks"), Filter = T("FileFilter") };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        foreach (var path in dialog.FileNames)
            if (!_books.Contains(path, StringComparer.OrdinalIgnoreCase)) _books.Add(path);
        RefreshBooks(); SaveDraft();
    }

    private void ChooseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = T("OutputFolder") };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) { _output.Text = dialog.FolderName; SaveDraft(); }
    }

    private void RemoveBook()
    {
        if (_list.SelectedValue is not string path) return;
        _books.Remove(path); RefreshBooks(); SaveDraft();
    }

    private void RefreshBooks()
    {
        var selected = _list.SelectedValue as string;
        _list.ItemsSource = _books.Select(path => new BookItem(path, Path.GetFileName(path))).ToArray();
        _list.SelectedValue = selected is not null && _books.Contains(selected) ? selected : _books.FirstOrDefault();
        _count.Text = string.Format(T("BookCount"), _books.Count);
        _remove.IsEnabled = _books.Count > 0;
        if (_books.Count == 0) { _source.Text = T("NoBooks"); _preview.Text = T("NoBooks"); _view.IsEnabled = false; }
        RefreshProcessingState();
    }

    private async Task ShowBookAsync()
    {
        var version = ++_previewVersion;
        _previewCancellation?.Cancel();
        if (_list.SelectedValue is not string path) return;
        var cancellation = new CancellationTokenSource(); _previewCancellation = cancellation;
        try
        {
            var file = new FileInfo(path); _view.IsEnabled = file.Exists;
            _source.Text = file.Exists ? $"{file.Name}\n{RagBookComponentsControl.Size(file.Length)}\n{T("DraftOnly")}" : T("SourceMissing");
            if (!file.Exists) { _preview.Text = T("SourceMissing"); return; }
            if (RagLexiconPackage.IsPackage(path)) { _preview.Text = LexiconPreview(path); return; }
            if (!string.Equals(file.Extension, ".txt", StringComparison.OrdinalIgnoreCase))
            { _preview.Text = T("PreviewOther"); return; }
            _preview.Text = T("Reading");
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(input, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[16001]; var count = await reader.ReadBlockAsync(buffer.AsMemory(), cancellation.Token);
            if (version == _previewVersion) _preview.Text = new string(buffer, 0, Math.Min(count, 16000))
                + (count <= 16000 ? "" : "\n\n" + T("PreviewLimited"));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException)
        { if (version == _previewVersion) { _preview.Text = T("SourceReadError"); _view.IsEnabled = false; } }
        finally
        {
            if (ReferenceEquals(_previewCancellation, cancellation)) _previewCancellation = null;
            cancellation.Dispose();
        }
    }

    private void SaveDraft()
    {
        SaveForExit();
        if (_processing is null && _canSave)
        {
            try { _library = RagBookPreparation.LibraryDirectory(_name.Text, _output.Text); ShowLibrary(); }
            catch (InvalidDataException error) { _status.Text = ErrorMessage(error); }
            RefreshProcessingState();
        }
    }
    public bool SaveForExit()
    {
        if (!_canSave) return true; // The unreadable original is retained and the warning stays visible.
        try { _store.Save(new() { Name = _name.Text, OutputDirectory = _output.Text, Books = _books.ToArray(),
            CpuTargetPercent = _cpuTarget.SelectedItem is int target ? target : 50 }); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { _status.Text = T("DraftSaveError"); return false; }
    }

    internal static TextBlock Text(string value, bool heading = false)
    {
        var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, heading ? 8 : 4, 0, 8), FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        if (heading) text.SetResourceReference(TextBlock.FontSizeProperty, "UiSectionFontSize");
        return text;
    }
    internal static Border Surface(UIElement content)
    {
        var border = new Border { Child = content, Padding = new Thickness(16), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "LineBrush"); return border;
    }
    internal static Button ActionButton(string text, Action action, string id, bool primary = false)
    {
        var button = new Button(); ConfigureButton(button, text, action, id);
        if (primary) button.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
        return button;
    }
    private static void ConfigureButton(Button button, string text, Action action, string id)
    {
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        label.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(Button.Foreground))
            { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1) });
        button.Content = label;
        button.Padding = new Thickness(12, 8, 12, 8); button.Margin = new Thickness(0, 4, 8, 4);
        AutomationProperties.SetAutomationId(button, id); AutomationProperties.SetName(button, text);
        button.Click += (_, _) => action();
    }

    private sealed record BookItem(string Path, string Name);
}
