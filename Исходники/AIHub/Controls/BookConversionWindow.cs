using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using AIHub.Services.Books;
using Button = System.Windows.Controls.Button;
using Binding = System.Windows.Data.Binding;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using ListView = System.Windows.Controls.ListView;
using ListViewItem = System.Windows.Controls.ListViewItem;
using Panel = System.Windows.Controls.Panel;
using SelectionMode = System.Windows.Controls.SelectionMode;
using Orientation = System.Windows.Controls.Orientation;
using ProgressBar = System.Windows.Controls.ProgressBar;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls;

/// <summary>The interactive book utility. Quiet Explorer conversions use the service directly.</summary>
public sealed partial class BookConversionWindow : Window
{
    private readonly Func<string, string> _l;
    private readonly ObservableCollection<Entry> _entries = [];
    private readonly ListView _files = new() { SelectionMode = SelectionMode.Single };
    private readonly ComboBox _format = new() { MinWidth = 140, Margin = new(0, 3, 0, 10) };
    private readonly CheckBox _merge;
    private readonly TextBox _directory = new(), _title = new(), _author = new(), _language = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 7, 0, 0) };
    private readonly TextBox _report = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 70, MaxHeight = 180 };
    private readonly ProgressBar _progress = new() { Height = 5, Minimum = 0, Maximum = 1 };
    private readonly StackPanel _settings = new(), _basicOptions = new(), _expertOptions = new(), _mergeFields = new(), _coverFields = new();
    private readonly Grid _workspace = new();
    private readonly StackPanel _modePicker = new();
    private readonly List<Button> _editButtons = [];
    private readonly Button _start, _cancel, _open, _showFolder;
    private readonly Button _back;
    private CancellationTokenSource? _work;
    private bool _busy, _changingFormats, _closeWhenDone, _allowClose;
    private string? _coverPath;
    private BookConversionOptions _options = new();

    public BookConversionWindow(Window? owner, Func<string, string> l)
    {
        _l = l;
        if (owner is not null) Owner = owner;
        MusicWishUi.PrepareWindow(this, L("Title"), "BookConverter.Window", 1220);
        if (owner is not null) Resources.MergedDictionaries.Insert(0, owner.Resources);
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/BookConversionResources.xaml", UriKind.Relative) });
        Height = 840; MinWidth = 850; MinHeight = 600;
        var root = new DockPanel { Margin = new(18) }; Content = root;
        var heading = new DockPanel { Margin = new(0, 0, 0, 10) };
        _back = Action("ChooseMode", () => { ShowMode(null); return Task.CompletedTask; });
        DockPanel.SetDock(_back, Dock.Right); heading.Children.Add(_back);
        heading.Children.Add(MusicWishUi.Text(L("Title"), true));
        DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        var body = new Grid(); root.Children.Add(body);
        _modePicker.MaxWidth = 760; _modePicker.VerticalAlignment = VerticalAlignment.Center;
        _modePicker.Children.Add(MusicWishUi.Text(L("ChooseMode"), true));
        _modePicker.Children.Add(MusicWishUi.Text(L("ModeHint")));
        var simple = Action("Simple", () => { ShowMode(false); return Task.CompletedTask; });
        simple.MinHeight = 68; _modePicker.Children.Add(simple);
        _modePicker.Children.Add(MusicWishUi.Text(L("SimpleHint")));
        var smart = Action("Smart", EnterSmartAsync);
        smart.MinHeight = 68; _modePicker.Children.Add(smart);
        _modePicker.Children.Add(MusicWishUi.Text(L("SmartHint")));
        body.Children.Add(_modePicker);
        body.Children.Add(_workspace);
        _workspace.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _workspace.RowDefinitions.Add(new());
        _workspace.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var toolbar = new WrapPanel(); _workspace.Children.Add(toolbar);
        BuildSourceToolbar(toolbar);
        var columns = new Grid(); Grid.SetRow(columns, 1); _workspace.Children.Add(columns);
        columns.ColumnDefinitions.Add(new()); columns.ColumnDefinitions.Add(new() { Width = new GridLength(390) });
        ConfigureSourceList(); columns.Children.Add(_files);
        _files.SelectionChanged += (_, _) => RefreshActions();
        _files.MouseDoubleClick += (_, _) => { if (_files.SelectedItem is Entry { Output: { Length: > 0 } } row) OpenPath(row.Output); };
        _settings.Margin = new(15, 0, 2, 0);
        var settingsScroll = new ScrollViewer { Content = _settings, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(settingsScroll, 1); columns.Children.Add(settingsScroll);
        _merge = MusicWishUi.Check(L("Merge"), false, selected => { _mergeFields.Visibility = selected ? Visibility.Visible : Visibility.Collapsed; RefreshFormats(); });
        AutomationProperties.SetAutomationId(_merge, "BookConverter.Merge");
        BuildSettings();
        var footer = new StackPanel { Margin = new(0, 12, 0, 0) }; Grid.SetRow(footer, 2); _workspace.Children.Add(footer);
        var actions = new WrapPanel(); footer.Children.Add(actions);
        _start = Action("Start", ConvertAsync); actions.Children.Add(_start);
        _cancel = Action("Cancel", () => { _work?.Cancel(); _status.Text = L("CancelPending"); AppendLog(_status.Text); return Task.CompletedTask; }); actions.Children.Add(_cancel);
        _open = Action("OpenResult", () => { OpenResult(false); return Task.CompletedTask; }); actions.Children.Add(_open);
        _showFolder = Action("ShowFolder", () => { OpenResult(true); return Task.CompletedTask; }); actions.Children.Add(_showFolder);
        _open.ToolTip = _showFolder.ToolTip = L("OpenMarkedHint");
        footer.Children.Add(_progress); footer.Children.Add(_status);
        footer.Children.Add(CreateDiagnostics());
        AllowDrop = true;
        DragOver += (_, e) => { e.Effects = !_busy && e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None; e.Handled = true; };
        Drop += async (_, e) => { if (!_busy && e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths) await AddPathsAsync(paths); };
        Closing += OnClosing;
        RefreshFormats(); ShowMode(null);
    }

    private string L(string key) => _l("BookConverter." + key);
    private Button Action(string key, Func<Task> action)
    {
        var button = MusicWishUi.Button(L(key), "BookConverter." + key, () => { });
        button.Click += async (_, _) => { try { await action(); }
            catch (OperationCanceledException) { _status.Text = L("Cancelled"); AppendLog(_status.Text); }
            catch (Exception error) { ShowDiagnosticsError(error); } };
        return button;
    }
    private string Explain(string message)
    {
        if (!message.StartsWith("BookConverter.", StringComparison.Ordinal)) return message;
        var key = new string(message.TakeWhile(value => char.IsLetterOrDigit(value) || value == '.').ToArray());
        var translated = _l(key);
        return translated == key ? message : translated + message[key.Length..];
    }
    private void ShowMode(bool? smart)
    {
        _modePicker.Visibility = smart is null ? Visibility.Visible : Visibility.Collapsed;
        _smartMode = smart == true;
        _workspace.Visibility = smart is not null ? Visibility.Visible : Visibility.Collapsed;
        _back.Visibility = smart is null ? Visibility.Collapsed : Visibility.Visible;
    }
    private sealed class Entry(string path) : INotifyPropertyChanged
    {
        public string Path { get; } = path;
        public string Name => System.IO.Path.GetFileName(Path);
        private string _status = "", _output = "";
        private bool _isChecked = true, _isDropTarget;
        public bool IsChecked { get => _isChecked; set { if (_isChecked == value) return; _isChecked = value; Notify(nameof(IsChecked)); } }
        public bool IsDropTarget { get => _isDropTarget; set { if (_isDropTarget == value) return; _isDropTarget = value; Notify(nameof(IsDropTarget)); } }
        public string Status { get => _status; set { _status = value; Notify(nameof(Status)); Notify(nameof(ToolTip)); } }
        public string Output { get => _output; set { _output = value; Notify(nameof(ToolTip)); } }
        public string ToolTip => Path + "\n" + Status + (Output.Length > 0 ? "\n" + Output : "");
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Notify(string name) => PropertyChanged?.Invoke(this, new(name));
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_busy && !_allowClose) { e.Cancel = true; _closeWhenDone = true; _work?.Cancel(); _status.Text = L("CancelPending"); }
    }
}
