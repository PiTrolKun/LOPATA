using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using ListBox = System.Windows.Controls.ListBox;
using UserControl = System.Windows.Controls.UserControl;
using Orientation = System.Windows.Controls.Orientation;
using RichTextBox = System.Windows.Controls.RichTextBox;
using ProgressBar = System.Windows.Controls.ProgressBar;
using Brushes = System.Windows.Media.Brushes;

namespace AIHub.Controls;

public sealed partial class ImageUtilityControl : UserControl
{
    public const string BackgroundKind = "image-utility";
    private readonly ImageUtilityStore _store;
    private readonly ImageUtilityAiService _ai;
    private ImageUtilityProcessor _processor = null!;
    private ImageUtilityJob _job = new();
    private ImageUtilityPreferences _preferences = new();
    private readonly ObservableCollection<ImageUtilityRow> _rows = [];
    private readonly List<(string Text, bool Error, bool Success)> _events = [];
    private Func<string, string> _l = key => key;
    private string _generationFolder = "";
    private bool _configured, _busy, _adding, _selectedAiReady;
    private CancellationTokenSource? _cancel;
    private CancellationTokenSource? _importCancel;
    private string? _previousOutputFolder;
    private ListBox? _list;
    private RichTextBox? _terminal;
    private TextBlock? _status, _aiStatus;
    private ProgressBar? _progress;
    private ImageUtilityWalker? _walker;
    private StackPanel? _settingsPanel, _primaryPanel;
    private StackPanel? _sourceButtons;
    private ImageUtilityMethodWindow? _methodWindow;
    private Button? _start, _pause, _stop, _retry;
    private IReadOnlyList<ImageUtilityFormat> _formats = [];
    private ImageUtilityOptions Options => _job.Options;
    public event Action<string>? ViewFileRequested;
    public event Action? WorkspaceChanged;
    public bool IsBusy => _busy || _adding;

    public ImageUtilityControl() : this(new ImageUtilityStore(Path.Combine(AppDataPaths.BaseDirectory, "image-utility"))) { }
    public ImageUtilityControl(ImageUtilityStore store, ImageUtilityAiService? ai = null)
    {
        _store = store; _ai = ai ?? new ImageUtilityAiService();
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/SettingsResources.xaml", UriKind.Relative) });
        AutomationProperties.SetAutomationId(this, "ImageUtility.Page");
        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.V && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control
                && System.Windows.Input.Keyboard.FocusedElement is not TextBox)
            { e.Handled = true; try { await PasteAsync(); } catch (Exception error) { Error(error); } }
        };
    }
    public void Configure(Func<string, string> localize, StorageSettings storage, int connections, string generationFolder)
    {
        _l = localize; _generationFolder = generationFolder; _ai.MaximumParallelConnections = connections;
        _ai.ConfigureStorage(storage.Models.Locations.Select(x => x.Path).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? AppDataPaths.ComponentModelsDirectory);
        if (!_configured)
        {
            _preferences = _store.LoadPreferences();
            _job.Options = Clone(_preferences.Options);
            if (_job.Options.MethodId != _preferences.FavoriteMethodId)
            {
                _job.Options.MethodId = _preferences.FavoriteMethodId;
                _job.Options.Parameters = ImageUtilityCatalog.RecommendedParameters(Options.MethodId);
            }
            if (string.IsNullOrWhiteSpace(Options.ExportFolder))
                Options.ExportFolder = storage.Results.Locations.Select(x => x.Path).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))
                    ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            _processor = new ImageUtilityProcessor(ai: _ai); _configured = true;
        }
        Render();
        _ = RefreshFormatsAsync();
    }
    public void Localize(Func<string, string> localize) { _l = localize; if (_configured) Render(); }
    public void DownloadConnections(int value) => _ai.MaximumParallelConnections = value;
    public bool UsesArtifact(string id) => (_busy && ImageUtilityAiService.UsesArtifact(Options.MethodId, id)) || _methodWindow?.UsesArtifact(id) == true;
    public void DisposeRuntime() { _cancel?.Cancel(); _importCancel?.Cancel(); _walker?.SetCurrentValue(VisibilityProperty, Visibility.Collapsed); _ai.Dispose(); }
    private string L(string key) => _l(key.StartsWith("ImageUtility.", StringComparison.Ordinal) ? key : "ImageUtility." + key);
    private static ImageUtilityOptions Clone(ImageUtilityOptions options) => JsonSerializer.Deserialize<ImageUtilityOptions>(JsonSerializer.Serialize(options))!;
    private Button ActionButton(string key, Func<Task> action) => ImageUtilityUi.Button(L(key), key, action, Error);
    private Button ActionButton(string key, Action action) => ActionButton(key, () => { action(); return Task.CompletedTask; });
    private void SavePreferences()
    {
        // Reload the counter: the queue can have reserved a process number since the UI was opened.
        _preferences.LastProcessNumber = _store.LoadPreferences().LastProcessNumber;
        _preferences.Options = Clone(Options); _store.SavePreferences(_preferences);
    }
    private void Error(Exception error)
    {
        var message = error is ImageUtilityException known ? ImageUtilityUi.ErrorText(L, known.MessageKey, known.Message) : L("Error") + " " + error.Message;
        AppendLog(message, true); if (_status is not null) _status.Text = message;
    }
    private void AppendLog(string text, bool error = false, bool success = false)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {text}"; _events.Add((line, error, success));
        if (_events.Count > 500) _events.RemoveAt(0);
        if (_terminal is null) return;
        _terminal.Document.Blocks.Add(new Paragraph(new Run(line)) { Foreground = error ? Brushes.LightCoral : success ? Brushes.LightGreen : Brushes.Gainsboro, Margin = new(0, 0, 0, 4) });
        if (_terminal.Document.Blocks.Count > 500) _terminal.Document.Blocks.Remove(_terminal.Document.Blocks.FirstBlock);
        _terminal.ScrollToEnd();
    }
    private async Task RefreshFormatsAsync()
    {
        try
        {
            _formats = await _processor.GetFormatsAsync(CancellationToken.None);
            if (_formats.Count > 0 && !_formats.Any(x => x.Id == Options.Format)) { Options.Format = _formats[0].Id; SavePreferences(); }
            if (!_busy && _primaryPanel is not null) RenderPrimarySettings(_primaryPanel);
        }
        catch (Exception error) { Error(error); }
    }
    private void RefreshRows(bool rebuild = false)
    {
        if (rebuild)
        {
            var previous = _rows.ToDictionary(x => x.Item.Id); _rows.Clear();
            foreach (var item in _job.Items)
            {
                var row = previous.GetValueOrDefault(item.Id) ?? new ImageUtilityRow(item); _rows.Add(row);
                if (row.Thumbnail is null) TryThumbnail(row);
            }
        }
        foreach (var row in _rows) row.Refresh(L, Options);
        RefreshBackgroundStatus();
    }
}
