using System.Windows;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using ProgressBar = System.Windows.Controls.ProgressBar;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace AIHub.Controls;

public sealed partial class ImageGenerationControl
{
    private bool _preparationReady, _preparationChecked;
    private IReadOnlyList<ManagedModelArtifactCard> _preparationCards = [];
    private ProgressBar? _preparationProgress;

    private void RenderPreparation()
    {
        var panel = new StackPanel { MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(24, 24, 24, 12) };
        panel.Children.Add(Text(L("Preparation.Title") + " · " + ImageGenerationCatalog.Get(_modelId).Name, true));
        panel.Children.Add(Text(L("Preparation.Hint")));
        if (string.IsNullOrWhiteSpace(_modelsRoot)) panel.Children.Add(Text(L("StorageRequired")));
        foreach (var card in _preparationCards)
        {
            var state = !_preparationChecked ? L("Preparation.Checking")
                : card.Status == ManagedModelStatuses.Installed ? L("Preparation.Available") : L("Preparation.Missing");
            panel.Children.Add(Text(card.DisplayName + " — " + state));
        }
        _status = Text(_statusText); panel.Children.Add(_status);
        _preparationProgress = new ProgressBar { Height = 10, Minimum = 0, Maximum = 100,
            IsIndeterminate = _busy, Value = _preparationReady ? 100 : 0, Margin = new Thickness(0, 10, 0, 14) };
        _preparationProgress.SetResourceReference(ProgressBar.ForegroundProperty, "AccentBrush");
        _preparationProgress.SetResourceReference(ProgressBar.BackgroundProperty, "PanelBrush"); panel.Children.Add(_preparationProgress);
        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        if (!_preparationReady && _preparationChecked)
        {
            var download = Button("Download", () => PrepareAsync(true), "Download", !_busy && !string.IsNullOrWhiteSpace(_modelsRoot));
            download.SetResourceReference(StyleProperty, "SettingsPrimaryActionStyle"); actions.Children.Add(download);
        }
        actions.Children.Add(Button("Verify", CheckPreparationAsync, "Verify", !_busy && !string.IsNullOrWhiteSpace(_modelsRoot)));
        var next = Button("OpenChat", ContinuePreparedAsync, "OpenChat", !_busy && _preparationReady);
        next.SetResourceReference(StyleProperty, "SettingsPrimaryActionStyle"); actions.Children.Add(next);
        if (_busy) actions.Children.Add(Button("Cancel", CancelAsync, "Cancel"));
        panel.Children.Add(actions);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
    private IProgress<ManagedModelDownloadProgress> PreparationProgress()
    {
        var operation = _cancel;
        return new Progress<ManagedModelDownloadProgress>(p =>
        {
            void Report()
            {
                if (_page != 1 || !_busy || _cancel != operation) return;
                var card = _preparationCards.FirstOrDefault(c => c.ModelArtifactId == p.ModelArtifactId);
                Status((p.Stage.StartsWith("verif", StringComparison.Ordinal) ? L("Verifying") : L("Downloading"))
                    + " · " + (card?.DisplayName ?? p.FileName)
                    + $" · {p.DownloadedBytes / 1_000_000_000d:0.00} / {p.TotalBytes / 1_000_000_000d:0.00} GB");
                if (_preparationProgress is { } bar)
                { bar.IsIndeterminate = p.TotalBytes <= 0; if (p.TotalBytes > 0) bar.Value = 100d * p.DownloadedBytes / p.TotalBytes; }
            }
            if (Dispatcher.CheckAccess()) Report();
            else if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(Report);
        });
    }
    private async Task CheckPreparationAsync()
    {
        if (_busy) return;
        if (string.IsNullOrWhiteSpace(_modelsRoot)) { Status(L("StorageRequired")); return; }
        _busy = true; _cancel = new(); _preparationReady = _preparationChecked = false;
        Status(L("Verifying")); Render();
        try
        {
            _preparationCards = _installation.Register(_modelsRoot, _modelId); Render();
            _preparationCards = await _installation.CheckAsync(_modelsRoot, _modelId, PreparationProgress(), _cancel.Token);
            _cancel.Token.ThrowIfCancellationRequested();
            _preparationReady = _preparationCards.All(c => c.Status == ManagedModelStatuses.Installed);
            _preparationChecked = true; Status(L(_preparationReady ? "Preparation.Ready" : "Preparation.NeedsDownload"));
        }
        catch (OperationCanceledException) { Status(L("Canceled")); }
        catch (Exception ex) { Error(ex); }
        finally { _busy = false; _cancel.Dispose(); _cancel = null; Render(); }
    }
    private async Task ContinuePreparedAsync()
    {
        if (_busy || !_preparationReady) return;
        // License acceptance and runtime integrity are rechecked before model use.
        if (!await PrepareAsync(false)) return;
        if (_page != 1 || !_preparationReady || _busy) return;
        if (!EnsureOutputFolder()) return;
        _sessionDirectory ??= ImageGenerationSessionStore.Create(_resultsRoot);
        _page = 2; Status(""); Render();
    }
}
