using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using ProgressBar = System.Windows.Controls.ProgressBar;
using UserControl = System.Windows.Controls.UserControl;

namespace AIHub.Controls.AnimatedAssets;

internal sealed record AnimationPreparationModels(string AdvisorName, bool AdvisorPresent, string ArtistName,
    bool ArtistPresent, bool StorageConfigured);

internal sealed class AnimationPreparationControl : UserControl
{
    private Func<string, string> _localize;
    private readonly Func<AnimationPreparationModels> _inspectModels;
    private readonly Func<CancellationToken, Task> _verify;
    private readonly Func<IProgress<ManagedModelDownloadProgress>, CancellationToken, Task> _download;
    private readonly TextBlock _title = Text(heading: true);
    private readonly TextBlock _hint = Text();
    private readonly TextBlock _base = Text();
    private readonly TextBlock _advisor = Text();
    private readonly TextBlock _artist = Text();
    private readonly TextBlock _status = Text();
    private readonly ProgressBar _progress = new() { Height = 10, Maximum = 100, Margin = new(0, 10, 0, 14) };
    private readonly Button _check;
    private readonly Button _downloadModels;
    private readonly Button _continue;
    private AnimationPreparationModels? _models;
    private CancellationTokenSource? _operation;
    private string _statusKey = "Music.Verifying";
    private Exception? _error;
    private bool _baseReady;
    private bool _started;
    private bool _closed;

    internal event Action? ContinueRequested;

    internal AnimationPreparationControl(Func<string, string> localize, Func<AnimationPreparationModels> inspectModels,
        Func<CancellationToken, Task> verify, Func<IProgress<ManagedModelDownloadProgress>, CancellationToken, Task> download)
    {
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/AIHub;component/Controls/SettingsResources.xaml", UriKind.Relative)
        });
        _localize = localize;
        _inspectModels = inspectModels;
        _verify = verify;
        _download = download;
        _check = ActionButton("Check");
        _downloadModels = ActionButton("Download");
        _continue = ActionButton("Continue");
        _check.Click += async (_, _) => await CheckAsync();
        _downloadModels.Click += async (_, _) => await DownloadAsync();
        _continue.Click += (_, _) =>
        {
            if (_baseReady && _operation is null && !_closed) ContinueRequested?.Invoke();
        };

        var panel = new StackPanel { MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new(24) };
        foreach (var row in new[] { _title, _hint, _base, _advisor, _artist, _status }) panel.Children.Add(row);
        _progress.SetResourceReference(ProgressBar.ForegroundProperty, "AccentBrush");
        _progress.SetResourceReference(ProgressBar.BackgroundProperty, "PanelBrush");
        panel.Children.Add(_progress);
        var actions = new WrapPanel();
        foreach (var button in new[] { _check, _downloadModels, _continue }) actions.Children.Add(button);
        panel.Children.Add(actions);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetAutomationId(this, "Animation.Preparation.Page");
        ToolTipService.SetShowOnDisabled(_downloadModels, true);
        Loaded += async (_, _) =>
        {
            if (!_started) await CheckAsync();
        };
        Refresh();
    }

    internal void Configure(Func<string, string> localize)
    {
        if (_localize == localize) return;
        _localize = localize;
        Refresh();
    }

    internal Task CheckAsync() => RunAsync(download: false);
    internal Task DownloadAsync() => RunAsync(download: true);

    internal void Cancel()
    {
        _closed = true;
        _operation?.Cancel();
    }

    private bool ModelsMissing => _models is { AdvisorPresent: false } or { ArtistPresent: false };

    private async Task RunAsync(bool download)
    {
        if (_closed || _operation is not null || download && (!_baseReady || !ModelsMissing || _models?.StorageConfigured != true)) return;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ApplicationBackgroundOperations.ExitToken);
        _operation = operation;
        _started = true;
        if (!download) _baseReady = false;
        _error = null;
        _statusKey = download ? "Music.Downloading" : "Music.Verifying";
        Refresh();
        try
        {
            if (download)
            {
                await _download(new Progress<ManagedModelDownloadProgress>(item => ReportProgress(operation, item)), operation.Token);
            }
            else
            {
                await _verify(operation.Token);
            }
            operation.Token.ThrowIfCancellationRequested();
            _baseReady = true;
            _statusKey = "Animation.Preparation.Ready";
        }
        catch (OperationCanceledException)
        {
            _statusKey = "Music.Canceled";
        }
        catch (Exception error)
        {
            ReportFailure(error);
        }
        finally
        {
            _operation = null;
            if (!_closed)
            {
                try { _models = _inspectModels(); }
                catch (Exception error) { if (_error is null) ReportFailure(error); }
                Refresh();
            }
        }
    }

    private void ReportFailure(Exception error)
    {
        _error = error;
        _statusKey = "VideoEditor.PreparationFailed";
        ComponentAuditFailures.Report(error);
    }

    private void ReportProgress(CancellationTokenSource operation, ManagedModelDownloadProgress item)
    {
        if (_closed || !ReferenceEquals(_operation, operation)) return;
        var stage = _localize(item.Stage switch
        {
            "downloading" => "VideoEditor.PreparationDownloading",
            "restarting" => "VideoEditor.PreparationRestarting",
            "unpacking" => "VideoEditor.PreparationUnpacking",
            _ => "Music.Verifying"
        });
        _status.Text = string.Join(" · ", stage, item.FileName);
        _progress.IsIndeterminate = item.Stage != "downloading" || item.TotalBytes <= 0;
        if (!_progress.IsIndeterminate)
            _progress.Value = Math.Clamp(100d * item.DownloadedBytes / item.TotalBytes, 0, 100);
    }

    private void Refresh()
    {
        _title.Text = _localize("Animation.Preparation.Title");
        _hint.Text = _localize("Animation.Preparation.Hint");
        _base.Text = _localize("Animation.Preparation.Base") + " — " +
            _localize(_baseReady ? "Music.Available" : "Animation.Preparation.Unchecked");
        _advisor.Text = ModelLine("Visual.Advisor", _models?.AdvisorName, _models?.AdvisorPresent);
        _artist.Text = ModelLine("Visual.Artist", _models?.ArtistName, _models?.ArtistPresent);
        _status.Text = _localize(_statusKey) + (_error is null ? "" : "\n" + _localize(_error.Message));
        bool idle = _operation is null && !_closed;
        _progress.IsIndeterminate = !idle && !_closed;
        _progress.Value = _baseReady && idle ? 100 : 0;
        _check.Content = _localize("Music.Verify");
        _check.IsEnabled = idle;
        _downloadModels.Content = _localize("Animation.Preparation.Download");
        _downloadModels.Visibility = ModelsMissing ? Visibility.Visible : Visibility.Collapsed;
        _downloadModels.IsEnabled = idle && _baseReady && _models?.StorageConfigured == true;
        _downloadModels.ToolTip = _models?.StorageConfigured == false ? _localize("Generation.StorageRequired") : null;
        _continue.Content = _localize("Music.Continue");
        _continue.IsEnabled = idle && _baseReady;
    }

    private string ModelLine(string role, string? name, bool? present) => name is null
        ? _localize(role)
        : _localize(role) + " · " + name + " — " + _localize(present == true ? "Visual.Present" : "Visual.ComponentsMissing");

    private static TextBlock Text(bool heading = false)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = heading ? 23 : 16,
            FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal, Margin = new(0, 8, 0, 8) };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        return text;
    }

    private static Button ActionButton(string suffix)
    {
        var button = new Button { Padding = new(14, 8, 14, 8), Margin = new(0, 4, 8, 4) };
        AutomationProperties.SetAutomationId(button, "Animation.Preparation." + suffix);
        return button;
    }
}
