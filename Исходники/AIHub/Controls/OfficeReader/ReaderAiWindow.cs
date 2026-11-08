using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.OfficeReader;
using AIHub.Services.VideoEditor;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using FontFamily = System.Windows.Media.FontFamily;
using RadioButton = System.Windows.Controls.RadioButton;

namespace AIHub.Controls.OfficeReader;

/// <summary>Selections and preparation only. Document contents never enter this window.</summary>
internal sealed partial class ReaderAiWindow : Window
{
    private readonly Func<string, string> _l;
    private readonly ReaderAiSettingsStore _settingsStore;
    private readonly ManagedModelLibraryStore _library;
    private readonly StorageSettings _storage;
    private readonly ReaderAiSettings _settings;
    private readonly Func<Task<IReadOnlyList<ManagedModelArtifactCard>>> _loadCatalog;
    private readonly StackPanel _text = new(), _speech = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _apply = new(), _refresh = new();
    private readonly CancellationTokenSource _life = new();
    private IReadOnlyList<ManagedModelArtifactCard> _cards = [];
    private bool _busy;
    private string Root => _storage.Models.Locations.Select(x => x.Path?.Trim()).FirstOrDefault(x => !string.IsNullOrEmpty(x)) ?? "";
    private string T(string key) => _l("Reader.Ai." + key);

    internal ReaderAiWindow(Window owner, Func<string, string> localize, ReaderAiSettingsStore? settings = null,
        ManagedModelLibraryStore? library = null, StorageSettings? storage = null,
        Func<Task<IReadOnlyList<ManagedModelArtifactCard>>>? loadCatalog = null)
    {
        Owner = owner; Icon = owner.Icon; _l = localize;
        _settingsStore = settings ?? new(); _settings = _settingsStore.Load();
        _library = library ?? new(); _storage = storage ?? new StorageSettingsStore().LoadOrCreate();
        // Other scenarios register new models in this common library. Reading the picker must
        // not rewrite their installation paths or reset their verification state.
        _loadCatalog = loadCatalog ?? (() => Task.Run(() => _library.LoadAll()));
        Title = T("Title"); Width = 1150; Height = 780; MinWidth = 760; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Resources.MergedDictionaries.Add(owner.Resources);
        ApplySharedTheme(owner is ReaderWindow reader ? reader.IsDarkTheme : owner is OfficeEditor.EditorWindow editor && editor.IsDarkTheme);
        FontFamily = new FontFamily("Segoe UI"); FontSize = 14;
        AutomationProperties.SetAutomationId(this, "Office.Reader.AI.Window");
        Build();
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) => _life.Cancel();
    }
    private void Build()
    {
        var dock = new DockPanel { Margin = new(18) }; Content = dock;
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); dock.Children.Add(header);
        header.Children.Add(new TextBlock { Text = T("Title"), FontSize = 22, Margin = new(0, 0, 0, 8) });
        header.Children.Add(new TextBlock { Text = T("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12) });
        var bottom = new StackPanel { Margin = new(0, 12, 0, 0) }; DockPanel.SetDock(bottom, Dock.Bottom); dock.Children.Add(bottom);
        bottom.Children.Add(_status);
        var actions = new WrapPanel(); bottom.Children.Add(actions);
        _refresh.Content = T("Refresh"); _refresh.Click += async (_, _) => await RefreshAsync();
        _apply.Content = T("Apply"); _apply.Click += (_, _) => {
            try { _settingsStore.Save(_settings); DialogResult = true; }
            catch (Exception error) { _status.Text = Error(error); }
        };
        AutomationProperties.SetAutomationId(_apply, "Office.Reader.AI.Apply");
        actions.Children.Add(_refresh); actions.Children.Add(_apply);
        var cancel = new Button { Content = T("Cancel"), IsCancel = true }; actions.Children.Add(cancel);
        var columns = new Grid(); columns.ColumnDefinitions.Add(new()); columns.ColumnDefinitions.Add(new()); dock.Children.Add(columns);
        AddSection(columns, _text, T("Text"), 0); AddSection(columns, _speech, T("Speech"), 1);
    }
    private static void AddSection(Grid grid, StackPanel items, string title, int column)
    {
        var dock = new DockPanel { Margin = new(column == 0 ? 0 : 10, 0, column == 0 ? 10 : 0, 0) };
        Grid.SetColumn(dock, column); grid.Children.Add(dock);
        var heading = new TextBlock { Text = title, FontSize = 18, Margin = new(0, 0, 0, 10) };
        DockPanel.SetDock(heading, Dock.Top); dock.Children.Add(heading);
        dock.Children.Add(new ScrollViewer { Content = items, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
    }
    internal async Task RefreshAsync()
    {
        if (_busy) return; _busy = true; _refresh.IsEnabled = _apply.IsEnabled = false; _status.Text = T("Loading");
        try
        {
            var cards = await _loadCatalog();
            if (_life.IsCancellationRequested) return;
            _cards = cards; Populate(); _status.Text = "";
        }
        catch (Exception error) { if (!_life.IsCancellationRequested) _status.Text = Error(error); }
        finally { _busy = false; _refresh.IsEnabled = _apply.IsEnabled = true; }
    }
    private void Populate()
    {
        _text.Children.Clear(); _speech.Children.Clear();
        var models = ReaderAiModelCatalog.TextModels(_storage, _cards);
        // Retain a temporarily unavailable user choice; never silently replace it with a different model.
        if (!models.Any(m => m.Id == _settings.TextModel))
            _text.Children.Add(new TextBlock { Text = T("Unavailable") + " " + _settings.TextModel, TextWrapping = TextWrapping.Wrap });
        foreach (var model in models) AddModel(_text, model, false);
        foreach (var model in ReaderAiModelCatalog.SpeechModels(Root, _cards)) AddModel(_speech, model, true);
    }
    private void AddModel(StackPanel parent, ReaderAiModel model, bool speech)
    {
        var panel = new StackPanel { Margin = new(12) };
        var frame = new Border { CornerRadius = new(8), BorderThickness = new(1), Margin = new(0, 0, 0, 8), Child = panel };
        frame.SetResourceReference(Border.BackgroundProperty, "ReaderPanel"); frame.SetResourceReference(Border.BorderBrushProperty, "ReaderLine"); parent.Children.Add(frame);
        var select = new RadioButton { Content = new TextBlock { Text = model.Name + " · " + ReaderAiModelCatalog.Size(model.Card.TotalBytes), TextWrapping = TextWrapping.Wrap },
            GroupName = speech ? "ReaderSpeech" : "ReaderText", IsChecked = model.Id == (speech ? _settings.Speech.Selected : _settings.TextModel),
            FontWeight = FontWeights.SemiBold, ToolTip = T("SizeHint"), Margin = new(0, 0, 0, 6) };
        AutomationProperties.SetAutomationId(select, "Office.Reader.AI." + (speech ? "Speech." : "Text.") + model.Id);
        select.SetResourceReference(RadioButton.ForegroundProperty, "ReaderText");
        select.Checked += (_, _) => { if (speech) _settings.Speech.Selected = model.Id; else _settings.TextModel = model.Id; };
        panel.Children.Add(select);
        panel.Children.Add(new TextBlock { Text = (model.Default ? T("Default") + " · " : "") + T(model.Downloaded ? "Downloaded" : "Missing")
            + (model.Experimental ? " · " + T("Experimental") : ""), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = speech ? _l("VideoEditor.Voices." + VideoNarratorCatalog.Get(model.Id).DescriptionKey)
            : T(model.Default ? "CoreHint" : "TextHint"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 5, 0, 5) });
        var buttons = new WrapPanel(); panel.Children.Add(buttons);
        var prepare = new Button { Content = T("Prepare"), ToolTip = T("SizeHint") };
        AutomationProperties.SetAutomationId(prepare, "Office.Reader.AI.Prepare." + model.Id);
        prepare.Click += async (_, _) => { prepare.IsEnabled = false;
            try { await PrepareAsync(model, speech, _life.Token); if (!_life.IsCancellationRequested) await RefreshAsync(); }
            catch (OperationCanceledException) { }
            catch (Exception error) { _status.Text = Error(error); }
            finally { prepare.IsEnabled = true; }
        }; buttons.Children.Add(prepare);
        if (speech)
        {
            var configure = new Button { Content = T("Configure") };
            AutomationProperties.SetAutomationId(configure, "Office.Reader.AI.Configure." + model.Id);
            configure.Click += (_, _) => ConfigureVoice(model.Id); buttons.Children.Add(configure);
            panel.Children.Add(new TextBlock { Text = T("Voice") + ": " + (_settings.Speech.Profiles.GetValueOrDefault(model.Id)?.Options.Speaker
                ?? VideoNarratorCatalog.Get(model.Id).DefaultSpeaker), Margin = new(3, 5, 0, 0) });
        }
    }
    private string Error(Exception e) => e.Message.StartsWith("Reader.", StringComparison.Ordinal)
        || e.Message.StartsWith("ChatSpeech.", StringComparison.Ordinal) || e.Message.StartsWith("VideoEditor.", StringComparison.Ordinal) ? _l(e.Message) : e.Message;
}
