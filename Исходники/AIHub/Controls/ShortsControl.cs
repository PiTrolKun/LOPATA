using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Controls.VideoUpscale;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.Shorts;
using AIHub.Services.VideoEditor;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace AIHub.Controls;

internal sealed partial class ShortsControl : UserControl
{
    private readonly ComboBox _mode = new() { MinWidth = 175 };
    private readonly ComboBox _duration = new() { MinWidth = 95, Margin = new(12, 0, 0, 0) };
    private readonly TextBox _prompt = new() { AcceptsReturn = true, MinHeight = 105, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly StackPanel _songPanel = new();
    private readonly StackPanel _promptPanel = new();
    private readonly StackPanel _imageRows = new();
    private readonly TextBlock _songName = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 3) };
    private readonly TextBlock _range = new();
    private readonly UpscaleRange _timeline = new(compact: true) { Margin = new(0, 4, 0, 2) };
    private readonly CheckBox _fadeEnding = new() { Margin = new(0, 6, 0, 6) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 0) };
    private readonly ShortsProgressPanel _progressPanel;
    private ShortsProgress _progress = new();
    private readonly StackPanel _inputs = new();
    private readonly Button _create, _preview, _pause, _stop, _open;
    private readonly MediaPlayer _player = new();
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _playheadTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly List<(FrameworkElement Element, string Key)> _localized = [];
    private Func<string, string> _localize;
    private ShortsDraft _draft;
    private string _root = "";
    private int _connections;
    private double _songSeconds;
    private bool _binding;
    private bool _busy;
    private bool _previewBusy;
    private bool _playing;
    private string? _previewFile;
    private string? _output;
    private CancellationTokenSource? _operationCancel;
    private CancellationTokenSource? _songRead;

    internal ShortsControl(Func<string, string> localize, ShortsDraft? draft = null, ShortsProjects? projects = null)
    {
        _localize = localize;
        _projects = projects ?? ShortsProjects.Default;
        _draft = draft ?? _projects.LoadDraft();
        _migrateLegacyDraft = draft is null && _draft.ProjectId is null && ShortsProjects.HasContent(_draft);
        try { _project = _projects.Restore(_draft); }
        catch (Exception error) when (ShortsProjects.ExpectedReadError(error))
        {
            OwnedProcessRegistry.Log("shorts_project_read_failed", "Shorts", detail: error.Message);
            _project = _projects.New(_draft);
            _status.Text = _localize("Shorts.Projects.ReadError");
        }
        _draft = _project.Workspace;
        Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri("/AIHub;component/Controls/ShortsResources.xaml", UriKind.Relative) });
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        _progressPanel = new ShortsProgressPanel(localize) { Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(this, "Shorts.Workspace");
        var layout = new StackPanel { Margin = new(24), MaxWidth = 920, HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch };
        var heading = Label("Shorts.Title");
        heading.FontWeight = FontWeights.SemiBold;
        heading.SetResourceReference(TextBlock.FontSizeProperty, "UiSectionFontSize");
        layout.Children.Add(heading);
        layout.Children.Add(_inputs);
        AddProjectControls();
        var choices = Row();
        _inputs.Children.Add(choices);
        choices.Children.Add(_mode);
        choices.Children.Add(_duration);
        _inputs.Children.Add(_songPanel);
        _inputs.Children.Add(_promptPanel);
        _songPanel.Children.Add(Action("Shorts.ChooseSong", "Song", PickSongAsync));
        _songPanel.Children.Add(_songName);
        _songPanel.Children.Add(_range);
        _songPanel.Children.Add(_timeline);
        _preview = Action("Shorts.Listen", "Listen", ListenAsync);
        _songPanel.Children.Add(_preview);
        _promptPanel.Children.Add(Label("Shorts.Prompt"));
        _promptPanel.Children.Add(_prompt);
        _inputs.Children.Add(Action("Shorts.AddImages", "AddImages", ChooseImagesAsync));
        _inputs.Children.Add(_imageRows);
        AddOutputSettings();
        _inputs.Children.Add(_fadeEnding);
        AutomationProperties.SetAutomationId(_fadeEnding, "Shorts.FadeEnding");
        _localized.Add((_fadeEnding, "Shorts.FadeEnding"));
        _fadeEnding.Checked += (_, _) => ChangeFade(true);
        _fadeEnding.Unchecked += (_, _) => ChangeFade(false);
        AutomationProperties.SetAutomationId(_mode, "Shorts.Mode");
        AutomationProperties.SetAutomationId(_duration, "Shorts.Duration");
        AutomationProperties.SetAutomationId(_prompt, "Shorts.Prompt");
        AutomationProperties.SetAutomationId(_timeline, "Shorts.ExcerptStart");
        var commands = Row();
        layout.Children.Add(commands);
        _create = Action("Shorts.Create", "Create", StartAsync);
        _pause = Action("Shorts.Pause", "Pause", PauseOrResumeAsync);
        _stop = Action("Shorts.Stop", "Stop", StopAsync);
        _open = Action("Shorts.Open", "Open", OpenResultAsync);
        _create.SetResourceReference(StyleProperty, "SettingsPrimaryActionStyle");
        commands.Children.Add(_create);
        commands.Children.Add(_pause);
        commands.Children.Add(_stop);
        commands.Children.Add(_open);
        layout.Children.Add(_progressPanel);
        layout.Children.Add(_status);
        Content = new ScrollViewer { Content = layout, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveDraft(); };
        _mode.SelectionChanged += (_, _) => ChangeOptions();
        _duration.SelectionChanged += (_, _) => ChangeOptions();
        _prompt.TextChanged += (_, _) => { if (!_binding) Edit(_draft with { Prompt = _prompt.Text }); };
        _timeline.Changed += SelectRange;
        _playheadTimer.Tick += (_, _) =>
            _timeline.Position = _draft.Start + Math.Clamp(_player.Position.TotalSeconds, 0, _draft.SongSeconds);
        _player.MediaEnded += (_, _) => StopPreview();
        _player.MediaFailed += (_, _) => { StopPreview(); _status.Text = _localize("Shorts.PreviewFailed"); };
        Loaded += async (_, _) =>
        {
            if (_migrateLegacyDraft) SaveDraft();
            if (!_busy && _output is null) ShowLatestProjectResult();
            ReloadImages();
            if (_draft.FromSong && _timeline.Waveform is null) await ReadSongAsync();
        };
        Unloaded += (_, _) => { _referenceLoads?.Cancel(); _songRead?.Cancel(); StopPreview(); SaveDraft(); };
        if (_draft.OutputPath is null && _draft.JobId is { } pendingId)
        {
            try
            {
                var saved = ShortsJob.Load(pendingId);
                if (!File.Exists(saved.Output)) _suggestedOutput = saved.Output;
            }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { }
        }
        Configure(localize, "", 0);
    }

    internal void Configure(Func<string, string> localize, string root, int connections)
    {
        _localize = localize;
        _root = root;
        _connections = connections;
        _progressPanel.Configure(localize);
        foreach (var (element, key) in _localized)
            if (element is ContentControl control) control.Content = localize(key);
            else ((TextBlock)element).Text = localize(key);
        AutomationProperties.SetHelpText(_timeline, localize("Shorts.TimelineHint"));
        _timeline.ToolTip = localize("Shorts.TimelineHint");
        LoadFields();
        _projectName.Text = ProjectName(_project);
        if (!_busy) SetOutputDisplay();
    }

    private static StackPanel Row() => new() { Orientation = Orientation.Horizontal, Margin = new(0, 8, 0, 8) };

    private TextBlock Label(string key)
    {
        var label = new TextBlock { Text = _localize(key), Margin = new(0, 8, 0, 4) };
        _localized.Add((label, key));
        return label;
    }

    private Button Action(string key, string id, Func<Task> run)
    {
        var button = new Button { Content = _localize(key), Padding = new(12, 6, 12, 6), Margin = new(0, 5, 8, 5),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        button.SetResourceReference(StyleProperty, "SettingsActionButtonStyle");
        AutomationProperties.SetAutomationId(button, "Shorts." + id);
        _localized.Add((button, key));
        button.Click += async (_, _) =>
        {
            try { await run(); }
            catch (OperationCanceledException) { }
            catch (Exception error) { ShowError(error); }
        };
        return button;
    }

    private void LoadFields()
    {
        _binding = true;
        _mode.ItemsSource = new[] { _localize("Shorts.FromDescription"), _localize("Shorts.FromSong") };
        _mode.SelectedIndex = _draft.FromSong ? 1 : 0;
        var durations = ShortsJob.DurationPresets.Select(seconds => new ComboBoxItem
        {
            Content = seconds < 60 ? Format("Shorts.Seconds", seconds)
                : TimeSpan.FromSeconds(seconds).ToString(@"m\:ss", CultureInfo.InvariantCulture),
            Tag = seconds
        }).ToArray();
        _duration.ItemsSource = durations;
        _duration.SelectedItem = durations.FirstOrDefault(item => (int)item.Tag == _draft.Seconds) ?? durations[0];
        _prompt.Text = _draft.Prompt;
        _fadeEnding.IsChecked = _draft.FadeEnding;
        _binding = false;
        RefreshMode();
    }

    private void ChangeOptions()
    {
        if (_binding || _duration.SelectedItem is not ComboBoxItem { Tag: int duration }) return;
        StopPreview();
        Edit(_draft with
        {
            FromSong = _mode.SelectedIndex == 1,
            Seconds = duration,
            ExcerptFrames = duration == _draft.Seconds ? _draft.ExcerptFrames : null
        });
        RefreshMode();
        if (_draft.FromSong && _timeline.Waveform is null && _songRead?.IsCancellationRequested != false) _ = ReadSongAsync();
    }

    private void ChangeFade(bool enabled)
    {
        if (_binding) return;
        StopPreview();
        Edit(_draft with { FadeEnding = enabled });
    }

    private void RefreshMode()
    {
        _songPanel.Visibility = _draft.FromSong ? Visibility.Visible : Visibility.Collapsed;
        _promptPanel.Visibility = _draft.FromSong ? Visibility.Collapsed : Visibility.Visible;
        if (_draft.FromSong) BindRange();
        else _songRead?.Cancel();
        ReloadImages();
        RefreshActions();
    }

    private Task ChooseImagesAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Filter = "PNG, JPEG, WebP|*.png;*.jpg;*.jpeg;*.webp" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return Task.CompletedTask;
        var existing = _draft.Images;
        var added = dialog.FileNames.Except(existing.Select(image => image.Path), StringComparer.OrdinalIgnoreCase)
            .Select(path => new ShortsReference(path)).ToArray();
        var all = existing.Concat(added).ToArray();
        if (!_draft.FromSong && all.Length > ShortsJob.DescriptionImageLimit) throw new InvalidDataException("Shorts.ImageLimit");
        Edit(_draft with { References = all });
        ReloadImages();
        return Task.CompletedTask;
    }

    private async Task PickSongAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Audio|*.mp3;*.wav;*.flac;*.m4a;*.ogg;*.opus;*.aac|*.*|*.*" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        StopPreview();
        _songRead?.Cancel();
        _songSeconds = 0;
        _timeline.Waveform = null;
        Edit(_draft with { Song = dialog.FileName, Start = 0, ExcerptFrames = null });
        BindRange();
        await ReadSongAsync();
    }

    private async Task ReadSongAsync()
    {
        var source = _draft.Song;
        if (string.IsNullOrEmpty(source)) return;
        _songRead?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(ApplicationBackgroundOperations.ExitToken);
        _songRead = request;
        RefreshActions();
        var token = request.Token;
        try
        {
            await VideoRuntime.PrepareAsync(token);
            using var probe = await VideoRuntime.ProbeAsync(source, token);
            token.ThrowIfCancellationRequested();
            var stream = probe.RootElement.GetProperty("streams").EnumerateArray()
                .FirstOrDefault(item => item.GetProperty("codec_type").GetString() == "audio");
            var duration = ShortsJob.SongDuration(probe.RootElement);
            _songSeconds = duration;
            BindRange();
            RefreshActions();

            var streamIndex = stream.GetProperty("index").GetInt32();
            var waveform = await Task.Run(() => VideoWaveform.ReadAsync(source, streamIndex, duration, token), token);
            token.ThrowIfCancellationRequested();
            _timeline.Waveform = waveform;
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!token.IsCancellationRequested && _songSeconds == 0) ShowError(error);
            else if (!token.IsCancellationRequested)
                OwnedProcessRegistry.Log("shorts_waveform_failed", "Shorts", detail: error.Message);
        }
        finally
        {
            if (_songRead == request)
            {
                _songRead = null;
                RefreshActions();
            }
        }
    }

    private int MaximumExcerptFrames => (int)Math.Floor(Math.Min(_songSeconds, _draft.Seconds) * ShortsJob.Fps);
    private const int MinimumExcerptFrames = ShortsJob.FadeSeconds * ShortsJob.Fps;

    private void SelectRange(double start, double length)
    {
        if (_binding || MaximumExcerptFrames < MinimumExcerptFrames) return;
        var frames = Math.Clamp((int)Math.Round(length * ShortsJob.Fps), MinimumExcerptFrames, MaximumExcerptFrames);
        var snapped = ClampExcerptStart(start, _songSeconds, frames / (double)ShortsJob.Fps);
        var savedFrames = ExcerptOverride(frames);
        if (snapped != _draft.Start || savedFrames != _draft.ExcerptFrames)
        {
            StopPreview();
            Edit(_draft with { Start = snapped, ExcerptFrames = savedFrames });
        }
        BindRange();
    }

    private int? ExcerptOverride(int frames) => frames == _draft.Seconds * ShortsJob.Fps ? null : frames;

    private void BindRange()
    {
        _songName.Text = Path.GetFileName(_draft.Song);
        var fits = MaximumExcerptFrames >= MinimumExcerptFrames;
        var length = 0d;
        var selected = 0d;
        if (fits)
        {
            var frames = Math.Clamp(_draft.ExcerptFrames ?? _draft.Seconds * ShortsJob.Fps,
                MinimumExcerptFrames, MaximumExcerptFrames);
            length = frames / (double)ShortsJob.Fps;
            selected = ClampExcerptStart(_draft.Start, _songSeconds, length);
            var savedFrames = ExcerptOverride(frames);
            if (!ProjectsBusy && (selected != _draft.Start || savedFrames != _draft.ExcerptFrames))
                Edit(_draft with { Start = selected, ExcerptFrames = savedFrames });
        }

        _timeline.Set(_songSeconds, selected, length, ShortsJob.FadeSeconds, _draft.Seconds);
        _timeline.IsEnabled = fits;
        _timeline.Visibility = _songSeconds > 0 ? Visibility.Visible : Visibility.Collapsed;
        _preview.IsEnabled = fits;
        _range.Text = fits ? $"{FormatTime(selected)} – {FormatTime(selected + length)} · {Format("Shorts.ExcerptLength", length)}" : "";
        _range.Visibility = fits ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(_timeline, $"{_localize("Shorts.SelectedRange")} {_range.Text}");
    }

    internal static double ClampExcerptStart(double start, double songLength, double duration)
    {
        var maximum = Math.Max(0, Math.Floor((songLength - duration) * 100 + 1e-9) / 100);
        return Math.Clamp(double.IsFinite(start) ? Math.Round(start, 2) : 0, 0, maximum);
    }

    private static string FormatTime(double value) =>
        TimeSpan.FromSeconds(value).ToString(value >= 3600 ? @"hh\:mm\:ss\.ff" : @"mm\:ss\.ff");

    private string Format(string key, params object[] values) => string.Format(CultureInfo.CurrentCulture, _localize(key), values);

    private async Task ListenAsync()
    {
        if (_playing) { StopPreview(); return; }
        if (_previewBusy) return;
        var song = _draft;
        ShortsJob.ValidateRange(song.Start, song.SongSeconds, _songSeconds);
        _previewBusy = true;
        var path = Path.Combine(ShortsJob.Root, "Preview", Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            await ShortsJob.CutSongAsync(song.Song, path, song.Start, song.SongSeconds, ApplicationBackgroundOperations.ExitToken, song.FadeEnding);
            if (!IsLoaded || _draft != song) return;
            _previewFile = path;
            _player.Open(new Uri(path));
            _player.Play();
            _playing = true;
            _timeline.Position = song.Start;
            _playheadTimer.Start();
            _preview.Content = _localize("Shorts.StopListening");
        }
        finally
        {
            _previewBusy = false;
            if (_previewFile != path) DeletePreview(path);
        }
    }

    private void StopPreview()
    {
        _playheadTimer.Stop();
        _timeline.Position = double.NaN;
        _player.Close();
        if (_previewFile is { } path) DeletePreview(path);
        _previewFile = null;
        _playing = false;
        _preview.Content = _localize("Shorts.Listen");
    }

    private static void DeletePreview(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void Edit(ShortsDraft draft)
    {
        if (draft == _draft) return;
        _draft = draft with { JobId = null };
        _saveTimer.Stop();
        _saveTimer.Start();
        RefreshActions();
    }

    private void SaveDraft()
    {
        try { SaveProjectDraft(keepTemporary: _migrateLegacyDraft); }
        catch (Exception error) when (ShortsProjects.ExpectedReadError(error))
        {
            OwnedProcessRegistry.Log("shorts_project_save_failed", "Shorts", detail: error.Message);
            _status.Text = _localize("Shorts.Projects.WriteError");
        }
    }

    private BackgroundOperationState? Pending => _draft.JobId is { } id
        ? ApplicationBackgroundOperations.Current?.FindOperations(ShortsJob.Kind, id).FirstOrDefault(
            state => state.Phase is BackgroundOperationPhase.Running or BackgroundOperationPhase.Pausing
                or BackgroundOperationPhase.Paused or BackgroundOperationPhase.Waiting or BackgroundOperationPhase.Countdown)
        : null;

    internal void RefreshActions()
    {
        var phase = Pending?.Phase;
        var active = phase is BackgroundOperationPhase.Running or BackgroundOperationPhase.Pausing;
        var resume = phase is BackgroundOperationPhase.Waiting or BackgroundOperationPhase.Paused or BackgroundOperationPhase.Countdown;
        var readingDuration = _draft.FromSong && _songRead is not null && _songSeconds == 0;
        _create.IsEnabled = !_busy && !active && !resume && !readingDuration;
        _inputs.IsEnabled = !_busy && !active && !resume;
        _stop.Visibility = phase.HasValue ? Visibility.Visible : Visibility.Collapsed;
        _pause.Visibility = phase.HasValue ? Visibility.Visible : Visibility.Collapsed;
        _pause.IsEnabled = active && phase != BackgroundOperationPhase.Pausing || resume;
        _pause.Content = _localize(resume ? "Shorts.Resume" : "Shorts.Pause");
        _open.Visibility = _output is not null && File.Exists(_output) ? Visibility.Visible : Visibility.Collapsed;
        _progressPanel.Visibility = _busy || active || resume ? Visibility.Visible : Visibility.Collapsed;
        var stateKey = phase switch
        {
            BackgroundOperationPhase.Pausing => "Shorts.Pausing",
            BackgroundOperationPhase.Paused => "Shorts.Paused",
            BackgroundOperationPhase.Waiting or BackgroundOperationPhase.Countdown => "Shorts.Waiting",
            _ => null
        };
        if (_progressPanel.Visibility == Visibility.Visible)
            _progressPanel.Show(_progress, stateKey is null && (_busy || active), stateKey);
    }

    private async Task StartAsync()
    {
        if (_busy) return;
        if (ApplicationBackgroundOperations.Current is { BlocksNewWork: true } controller)
        {
            controller.ReportBlocked();
            return;
        }
        StopPreview();
        var draft = _draft;
        ShortsJob.Validate(draft);
        var token = ApplicationBackgroundOperations.ExitToken;
        var previous = draft.JobId is { } id ? ShortsJob.Load(id) : null;
        var retry = previous is not null && !File.Exists(previous.Output);
        if (!retry && draft.FromSong) ShortsJob.ValidateRange(draft.Start, draft.SongSeconds, _songSeconds);
        var output = ShortsJob.ValidateOutputPath(retry ? previous!.Output : SelectedOutput);
        _progress = new();
        _status.Text = "";
        _busy = true;
        RefreshActions();
        try
        {
            if (!await PrepareMissingComponentsAsync(retry ? previous!.ModelsRoot : _root, retry ? previous!.Connections : _connections, token))
                return;
            var job = retry ? previous! : await ShortsJob.CreateAsync(draft with { OutputPath = output, ProjectId = _project.Id },
                _root, _connections, token);
            RecordProjectJob(draft, job);
            await RunAsync(job, null, token);
        }
        finally { _busy = false; RefreshActions(); }
    }

    internal async Task ResumeAsync(BackgroundOperationState state, CancellationToken token)
    {
        var id = state.Input.Deserialize<string>() ?? throw new InvalidDataException("Shorts.InvalidJob");
        var job = ShortsJob.Load(id);
        _busy = true;
        try
        {
            RestoreJobProject(job);
            if (!File.Exists(job.Output) && !await PrepareMissingComponentsAsync(job.ModelsRoot, job.Connections, token))
                throw new BackgroundOperationWaitingException("Tray.NeedsInput");
            if (_draft.OutputPath is null) _suggestedOutput = job.Output;
            SaveProjectDraft();
            RefreshActions();
            await RunAsync(job, state, token);
        }
        finally { _busy = false; RefreshActions(); }
    }

    private async Task<bool> PrepareMissingComponentsAsync(string root, int connections, CancellationToken token) =>
        !await ShortsComponents.NeedsPreparationAsync(root, connections, token) ||
        ShortsComponentPreparation.Show(Window.GetWindow(this), _localize, root, connections, token);

    private async Task RunAsync(ShortsJob job, BackgroundOperationState? state, CancellationToken token)
    {
        SetOutputDisplay(job.Output);
        _status.Text = "";
        _progress = new();
        RefreshActions();
        var progress = new Progress<string>(message =>
        {
            if (!_busy) return;
            var next = _progress.Update(message);
            if (next == _progress) return;
            _progress = next;
            RefreshActions();
        });
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        _operationCancel = cancellation;
        try
        {
            _output = await ShortsRunner.StartAsync(job, _localize("Shorts.Title"),
                message => ((IProgress<string>)progress).Report(message), cancellation.Token, state);
            _status.Text = Format("Shorts.Ready", _output);
            if (_draft.OutputPath is null)
            {
                _suggestedOutput = null;
                SetOutputDisplay();
            }
        }
        finally { _operationCancel = null; }
    }

    private async Task PauseOrResumeAsync()
    {
        if (Pending is not { } state || ApplicationBackgroundOperations.Current is not { } controller) return;
        if (state.Phase is BackgroundOperationPhase.Running) await controller.PauseAsync(state.Id);
        else await controller.ResumeAsync(state.Id, ApplicationBackgroundOperations.ExitToken);
    }

    private Task StopAsync()
    {
        if (Pending is not { } state) return Task.CompletedTask;
        if (_operationCancel is not null) _operationCancel.Cancel();
        else ApplicationBackgroundOperations.Current?.DiscardPending(state.Id);
        _status.Text = _localize("SongClip.Canceled");
        return Task.CompletedTask;
    }

    internal bool ShowResult(string id)
    {
        var job = ShortsJob.Load(id);
        if (!File.Exists(job.Output)) return false;
        var owner = _projects.FindOwner(job.ProjectId, id);
        if (!ProjectsBusy && owner is not null && owner.Id != _project.Id)
        {
            SaveProjectDraft(keepTemporary: true);
            ActivateProject(owner, owner.Workspace);
            SaveProjectDraft();
        }
        return DisplayResult(job);
    }

    private bool DisplayResult(ShortsJob job)
    {
        if (!File.Exists(job.Output)) return false;
        _output = job.Output;
        _status.Text = Format("Shorts.Ready", _output);
        RefreshActions();
        return true;
    }

    private Task OpenResultAsync()
    {
        if (_output is { } file && File.Exists(file)) Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    private void ShowError(Exception error)
    {
        OwnedProcessRegistry.Log("shorts_failed", "Shorts", detail: error.Message);
        var localized = _localize(error.Message);
        _status.Text = localized == error.Message ? _localize("Shorts.Failed") : localized;
    }
}
