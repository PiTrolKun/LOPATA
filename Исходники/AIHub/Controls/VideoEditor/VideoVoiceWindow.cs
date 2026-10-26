using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AIHub.Services;
using AIHub.Services.VideoEditor;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using ListBox = System.Windows.Controls.ListBox;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;
using Control = System.Windows.Controls.Control;

namespace AIHub.Controls.VideoEditor;

/// <summary>Local preview lifetime. It cannot change a project; Apply returns a profile to the owner.</summary>
internal sealed partial class VideoVoiceWindow : Window
{
    private readonly Func<string, string> _l;
    private readonly ManagedModelLibraryStore _store;
    private readonly Func<VideoModelNeed, CancellationToken, Task<VideoPreparedModels?>>? _prepare;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ListBox _models = new();
    private readonly ComboBox _speaker = new(), _language = new(), _preset = new(), _device = new();
    private sealed record PresetChoice(string Id, string Name);
    private readonly TextBox _rate = new() { Text = "100" }, _style = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 65 }, _steps = new() { Text = "8" };
    private readonly CheckBox _global = new();
    private readonly StackPanel _details = new(), _trialPanel = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _description = new() { TextWrapping = TextWrapping.Wrap }, _status = new() { TextWrapping = TextWrapping.Wrap }, _sample = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _apply = new(), _download = new(), _remove = new(), _generate = new(), _play = new(), _stop = new();
    private readonly TextBox _trial = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 85, MaxLength = 2000 };
    private readonly MediaPlayer _player = new();
    private readonly Dictionary<string, VideoNarratorChoice> _drafts = [];
    private readonly Dictionary<string, TextBlock> _cardStatuses = [];
    private readonly Dictionary<string, (TextBlock Label, Control Control)> _fields = [];
    private readonly bool _canTargetSelection;
    private CancellationTokenSource? _trialCancel;
    private string? _audio;
    private bool _busy;
    private VideoNarratorModel? Model => (_models.SelectedItem as ListBoxItem)?.Tag as VideoNarratorModel;
    internal VideoNarratorChoice? Choice { get; private set; }
    internal bool Global => _global.IsChecked == true;
    private string L(string key) => _l("VideoEditor.Voices." + key);

    internal VideoVoiceWindow(Window owner, Func<string, string> localize, VideoNarratorChoice initial, bool global,
        Func<VideoModelNeed, CancellationToken, Task<VideoPreparedModels?>>? prepare, ManagedModelLibraryStore? library = null)
    {
        Owner = owner; _l = localize; _prepare = prepare; _store = library ?? new(); _canTargetSelection = !global;
        MusicWishUi.PrepareWindow(this, _l("VideoEditor.VoiceSettings"), "VideoEditor.Voices.Window", 1050);
        Height = 760; MinWidth = 800; MinHeight = 600; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Resources.MergedDictionaries.Add(owner.Resources); SetResourceReference(BackgroundProperty, "WindowBackgroundBrush"); SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/VideoEditor/VideoEditorResources.xaml", UriKind.Relative) });
        SetResourceReference(FontSizeProperty, "UiBodyFontSize");
        ScrollViewer.SetHorizontalScrollBarVisibility(_models, ScrollBarVisibility.Disabled);
        _drafts[initial.Id] = initial; _global.IsChecked = global; _global.Content = _l("VideoEditor.GlobalVoice");
        _global.ToolTip = L("ScopeHint");
        _preset.ItemsSource = new[] { new PresetChoice("stable", L("Stable")), new PresetChoice("expressive", L("Expressive")) }; _preset.DisplayMemberPath = "Name";
        _device.ItemsSource = new[] { new PresetChoice("auto", L("Auto")), new PresetChoice("gpu", L("Gpu")), new PresetChoice("cpu", L("Cpu")) }; _device.DisplayMemberPath = "Name"; _device.SelectedValuePath = "Id";
        _language.DisplayMemberPath = "Name"; _language.SelectedValuePath = "Id";
        Build(); Populate();
        _models.SelectionChanged += (_, e) => { if (e.RemovedItems.Cast<ListBoxItem>().FirstOrDefault()?.Tag is VideoNarratorModel prior)
            try { _drafts[prior.Id] = ReadChoice(prior); } catch (InvalidOperationException) { } catch (System.IO.InvalidDataException) { }
            LoadModel(); };
        _models.SelectedItem = _models.Items.Cast<ListBoxItem>().FirstOrDefault(i => ((VideoNarratorModel)i.Tag).Id == initial.Id);
        if (_models.SelectedItem is null) _status.Text = _l("VideoEditor.VoiceUnavailable");
        _player.MediaFailed += (_, e) => { _exampleStop.IsEnabled = false; _status.Text = e.ErrorException.Message; };
        _player.MediaEnded += (_, _) => _exampleStop.IsEnabled = false;
        Closing += (_, e) => { if (_busy) { _trialCancel?.Cancel(); e.Cancel = true; _status.Text = L("Stopping"); } };
        Closed += (_, _) => { _lifetime.Cancel(); _trialCancel?.Cancel(); _player.Close(); };
    }
    private void Build()
    {
        var grid = new Grid { Margin = new(16) }; grid.ColumnDefinitions.Add(new() { Width = new(350) }); grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); Content = grid;
        var left = new DockPanel { Margin = new(0, 0, 16, 0) }; grid.Children.Add(left);
        var title = new TextBlock { Text = L("Catalog"), FontSize = 18, Margin = new(0, 0, 0, 10) }; DockPanel.SetDock(title, Dock.Top); left.Children.Add(title); left.Children.Add(_models);
        var exampleHeader = new StackPanel(); DockPanel.SetDock(exampleHeader, Dock.Top);
        exampleHeader.Children.Add(new TextBlock { Text = L("RecordedExamplesHint"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 6) });
        _exampleStop.Content = L("Stop"); _exampleStop.IsEnabled = false; _exampleStop.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        System.Windows.Automation.AutomationProperties.SetAutomationId(_exampleStop, "VideoEditor.Voices.Sample.Stop");
        _exampleStop.Margin = new(0, 0, 0, 8); _exampleStop.Click += (_, _) => StopExample(); exampleHeader.Children.Add(_exampleStop);
        // The model list remains the last DockPanel child and fills the remaining space.
        left.Children.Insert(left.Children.Count - 1, exampleHeader);
        var right = new DockPanel(); Grid.SetColumn(right, 1); grid.Children.Add(right);
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); right.Children.Add(bottom);
        bottom.Children.Add(new ScrollViewer { Content = _status, MaxHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); _apply.Content = _l("VideoEditor.Apply"); _apply.Margin = new(0, 12, 0, 0);
        _apply.Click += (_, _) => { try { Choice = ReadChoice(); DialogResult = true; } catch (Exception e) { _status.Text = Error(e); } }; bottom.Children.Add(_apply);
        right.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _details });
        _details.Children.Add(_description);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 10, 0, 10) };
        _download.Content = L("Download"); _remove.Content = L("Remove");
        _download.Click += (_, _) => Run(DownloadAsync); _remove.Click += (_, _) => Run(RemoveAsync);
        actions.Children.Add(_download); actions.Children.Add(_remove); _details.Children.Add(actions);
        AddField("Speaker", _speaker); AddField("Language", _language); AddField("Device", _device);
        AddField("Rate", _rate); AddField("Style", _style); AddField("Steps", _steps); AddField("Preset", _preset); _details.Children.Add(_global);
        _sample.Margin = new(0, 12, 0, 8); _details.Children.Add(_sample);
        var check = new Button { Content = L("CheckVoice"), Margin = new(0, 5, 0, 8) };
        check.Click += (_, _) => _trialPanel.Visibility = _trialPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        _details.Children.Add(check); _details.Children.Add(_trialPanel); _trialPanel.Children.Add(_trial);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        _generate.Content = L("Synthesize"); _play.Content = L("Play"); _stop.Content = L("Stop");
        _generate.Click += (_, _) => Run(TrialAsync); _play.Click += (_, _) => Play(); _stop.Click += (_, _) => { _trialCancel?.Cancel(); StopExample(); };
        foreach (var b in new[] { _generate, _play, _stop }) { b.Margin = new(0, 8, 8, 8); buttons.Children.Add(b); }
        _trialPanel.Children.Add(buttons); _trialPanel.Children.Add(new TextBlock { Text = L("TrialSafe"), TextWrapping = TextWrapping.Wrap });
        _speaker.SelectionChanged += (_, _) => InvalidatePreview(); _language.SelectionChanged += (_, _) => InvalidatePreview();
        _device.SelectionChanged += (_, _) => InvalidatePreview(); _rate.TextChanged += (_, _) => InvalidatePreview();
        _style.TextChanged += (_, _) => InvalidatePreview(); _steps.TextChanged += (_, _) => InvalidatePreview(); _trial.TextChanged += (_, _) => InvalidatePreview();
        _preset.SelectionChanged += (_, _) => InvalidatePreview();
    }
    private void AddField(string label, Control control)
    {
        var title = new TextBlock { Text = L(label), Margin = new(0, 8, 0, 3) }; _details.Children.Add(title); control.MinHeight = 30;
        _fields[label] = (title, control);
        control.ToolTip = L(label); System.Windows.Automation.AutomationProperties.SetName(control, L(label)); _details.Children.Add(control);
    }
    private void Populate()
    {
        foreach (var m in VideoNarratorCatalog.Models)
        {
            var card = new StackPanel { Margin = new(8) }; card.Children.Add(new TextBlock { Text = m.Name, FontWeight = FontWeights.SemiBold });
            card.Children.Add(new TextBlock { Text = string.Join(" / ", m.Languages), TextWrapping = TextWrapping.Wrap });
            var status = new TextBlock { Text = m.Blocked is not null ? L("Pending") : Installed(m) ? L("Installed") : L("Missing"), TextWrapping = TextWrapping.Wrap };
            _cardStatuses[m.Id] = status; card.Children.Add(status);
            card.Children.Add(ExampleControls(m));
            _models.Items.Add(new ListBoxItem { Content = card, Tag = m, HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch });
        }
    }
    private bool Installed(VideoNarratorModel model) => _store.Load(VideoNarratorCatalog.ArtifactId(model.Id))?.Status == AIHub.Models.ManagedModelStatuses.Installed;
    private void LoadModel()
    {
        _player.Stop(); InvalidatePreview(); var m = Model; if (m is null) return;
        var initial = _drafts.GetValueOrDefault(m.Id) ?? new(m.Id, m.Languages.Contains("ru") ? "ru" : m.Languages[0], 100, new(Speaker: m.DefaultSpeaker));
        _description.Text = m.Name + "\n\n" + L(m.DescriptionKey) + "\n\n" + L("License") + ": " + m.License;
        _speaker.ItemsSource = m.Speakers; _speaker.SelectedItem = string.IsNullOrEmpty(initial.Options.Speaker) ? m.DefaultSpeaker : initial.Options.Speaker;
        _language.ItemsSource = m.Languages.Select(code => new PresetChoice(code, LanguageName(code))).ToArray(); _language.SelectedValue = initial.Language;
        _rate.Text = initial.Rate.ToString(CultureInfo.InvariantCulture); _rate.IsEnabled = m.Rate;
        _style.Text = initial.Options.Style; _style.IsEnabled = m.Style;
        _steps.Text = initial.Options.Steps.ToString(CultureInfo.InvariantCulture); _steps.IsEnabled = m.Steps;
        _preset.SelectedItem = _preset.Items.Cast<PresetChoice>().Single(p => p.Id == initial.Options.Preset);
        _device.SelectedValue = initial.Options.Device; _device.IsEnabled = true;
        ShowField("Rate", m.Rate); ShowField("Style", m.Style); ShowField("Steps", m.Steps);
        ShowField("Preset", m.Preset);
        ShowField("Device", true);
        _sample.Text = L("NoSample"); _status.Text = m.Blocked is {} blocked ? L(blocked) : L("NotPhysicallyTested"); UpdateButtons();
    }
    private void ShowField(string name, bool visible)
    { var field = _fields[name]; field.Label.Visibility = field.Control.Visibility = visible ? Visibility.Visible : Visibility.Collapsed; }
    private static string LanguageName(string code)
    { try { return code + " — " + CultureInfo.GetCultureInfo(code).NativeName; } catch (CultureNotFoundException) { return code; } }
    private VideoNarratorChoice ReadChoice(VideoNarratorModel? selected = null)
    {
        var m = selected ?? Model ?? throw new InvalidOperationException("VideoEditor.VoiceUnavailable");
        if (m.Blocked is not null || _speaker.SelectedItem is not string speaker || _language.SelectedValue is not string language
            || !int.TryParse(_rate.Text, out var rate) || !int.TryParse(_steps.Text, out var steps)) throw new InvalidOperationException("VideoEditor.VoiceUnavailable");
        var result = new VideoNarratorChoice(m.Id, language, rate, new(speaker, _style.Text, _device.SelectedValue as string ?? "auto", steps, (_preset.SelectedItem as PresetChoice)?.Id ?? "stable"));
        VideoNarratorCatalog.Validate(result.Id, result.Language, result.Rate, result.Options); _drafts[m.Id] = result; return result;
    }
    private void InvalidatePreview() { StopExample(); _audio = null; _play.IsEnabled = false; _sample.Text = L("NoSample"); }
    private void UpdateButtons()
    {
        var m = Model; var available = !_busy && m is { Blocked: null };
        _models.IsEnabled = !_busy; _apply.IsEnabled = available; _download.IsEnabled = available && _prepare is not null; _generate.IsEnabled = available;
        _remove.IsEnabled = available && m is not null && !m.Id.StartsWith("kokoro-", StringComparison.Ordinal) && _store.Load(VideoNarratorCatalog.ArtifactId(m.Id)) is not null;
        _play.IsEnabled = !_busy && _audio is not null;
        foreach (var button in _exampleButtons) button.IsEnabled = !_busy && button.Tag is VideoNarratorSample;
        _speaker.IsEnabled = _language.IsEnabled = _trial.IsEnabled = available;
        _global.IsEnabled = available && _canTargetSelection;
        _rate.IsEnabled = !_busy && m?.Rate == true; _style.IsEnabled = !_busy && m?.Style == true; _steps.IsEnabled = !_busy && m?.Steps == true;
        _preset.IsEnabled = available && m?.Preset == true;
        _device.IsEnabled = available;
        foreach (var model in VideoNarratorCatalog.Models)
            if (_cardStatuses.TryGetValue(model.Id, out var status)) status.Text = model.Blocked is not null ? L("Pending") : Installed(model) ? L("Installed") : L("Missing");
    }
    private string Error(Exception error) => error.Message.StartsWith("VideoEditor.", StringComparison.Ordinal) ? _l(error.Message) : error.Message;
    private async void Run(Func<Task> action)
    {
        using var audit = ComponentAudit.CaptureOperation();
        if (_busy) return; _busy = true; _trialCancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); UpdateButtons();
        try { await action(); }
        catch (OperationCanceledException) { _status.Text = L("Cancelled"); }
        catch (Exception e) { VideoErrors.Report(e); _status.Text = Error(e); }
        finally { _busy = false; _trialCancel.Dispose(); _trialCancel = null; UpdateButtons(); }
    }
    private CancellationToken Token => _trialCancel?.Token ?? _lifetime.Token;
    private async Task DownloadAsync()
    {
        var choice = ReadChoice(); if (_prepare is null) return;
        var prepared = await _prepare(new("speech", [choice.Id]), Token);
        if (prepared is not null) _status.Text = L("Prepared");
    }
    private Task RemoveAsync()
    {
        var m = Model!;
        if (System.Windows.MessageBox.Show(this, L("RemoveConfirm"), m.Name, MessageBoxButton.YesNo) == MessageBoxResult.Yes)
        {
            var guard = new DelegateModelUsageGuard(_ => ApplicationBackgroundOperations.Current?.HasPending == true);
            new ManagedModelRemovalService(_store, guard).RemoveFiles(VideoNarratorCatalog.ArtifactId(m.Id), includePartialFiles: true);
            _status.Text = L("Removed");
        }
        return Task.CompletedTask;
    }
    private async Task TrialAsync()
    {
        if (ApplicationBackgroundOperations.Current?.HasPending == true) throw new InvalidOperationException("VideoEditor.Busy");
        var choice = ReadChoice(); if (string.IsNullOrWhiteSpace(_trial.Text)) { _status.Text = L("EnterText"); return; }
        if (_prepare is not null && await _prepare(new("speech", [choice.Id]), Token) is null) return;
        InvalidatePreview(); _status.Text = L("Speaking");
        using var audit = ComponentAudit.CaptureOperation(); using var narrator = new VideoNarratorRegistry(_store).Create(choice.Id);
        var result = narrator is IConfiguredVideoNarrator configured
            ? await configured.GenerateAsync(choice.Id, choice.Language, _trial.Text, choice.Rate, choice.Options, Token)
            : await narrator.GenerateAsync(choice.Id, choice.Language, _trial.Text, choice.Rate, Token);
        Token.ThrowIfCancellationRequested(); _audio = result.Path; _sample.Text = L("SampleReady"); _status.Text = L("TrialReady");
        Play();
    }
    private void Play() { if (_audio is null) return; StopExample(); _player.Open(new Uri(_audio)); _player.Play(); }
}
