using System.Windows;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;

namespace AIHub.Controls;

public sealed partial class MusicPoetryModelWindow
{
    private readonly Func<MusicPoetryModelCandidate, IProgress<ManagedModelDownloadProgress>, CancellationToken, Task<DebugModelInfo>>? _download;
    private readonly Func<string, string> _downloadLocalize;
    private readonly Func<string, string> _localizeDownloadError;
    private readonly TextBlock _downloadStatus = new() { Margin = new(0, 8, 0, 2) };
    private readonly System.Windows.Controls.ProgressBar _downloadProgress = new() { Height = 6, Maximum = 100, Visibility = Visibility.Collapsed };
    private readonly List<Button> _modelActions = [], _downloadButtons = [];
    private CancellationTokenSource? _downloadCancel;
    private bool _closeAfterDownload;
    public bool IsDownloading => _downloadCancel is not null;

    internal static string FormatDownloadSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)Math.Max(0, bytes); var unit = 0;
        while (value >= 1000 && unit < units.Length - 1) { value /= 1000; ++unit; }
        return $"{value:0.##} {units[unit]}";
    }

    private async Task DownloadAsync(MusicPoetryModelCandidate candidate, Button active, Action<DebugModelInfo> installed)
    {
        if (IsDownloading) { _downloadCancel?.Cancel(); return; }
        if (_download is null || IsRemoving) return;
        _downloadCancel = CancellationTokenSource.CreateLinkedTokenSource(ApplicationBackgroundOperations.ExitToken);
        var enabled = _modelActions.ToDictionary(button => button, button => button.IsEnabled);
        foreach (var action in _modelActions) action.IsEnabled = false;
        foreach (var button in _downloadButtons) button.IsEnabled = ReferenceEquals(button, active);
        RefreshRemoveButtons();
        active.Content = _downloadLocalize("PauseDownload"); _downloadProgress.Visibility = Visibility.Visible;
        _downloadProgress.IsIndeterminate = true; _downloadStatus.Text = _downloadLocalize("Downloading");
        DebugModelInfo? model = null;
        try
        {
            model = await _download(candidate, new Progress<ManagedModelDownloadProgress>(p => {
                _downloadProgress.IsIndeterminate = p.TotalBytes <= 0;
                if (p.TotalBytes > 0) _downloadProgress.Value = Math.Clamp(100d * p.DownloadedBytes / p.TotalBytes, 0, 100);
                _downloadStatus.Text = $"{p.FileName} · {_downloadProgress.Value:0}% · "
                    + FormatDownloadSize(p.DownloadedBytes) + " / "
                    + FormatDownloadSize(p.TotalBytes > 0 ? p.TotalBytes : candidate.DownloadBytes);
            }), _downloadCancel.Token);
            _downloadStatus.Text = _downloadLocalize("Downloaded");
        }
        catch (OperationCanceledException) { _downloadStatus.Text = _downloadLocalize("DownloadPaused"); }
        catch (Exception error) { _downloadStatus.Text = _downloadLocalize("DownloadError") + " " + _localizeDownloadError(error.Message); }
        finally
        {
            _downloadCancel.Dispose(); _downloadCancel = null; _downloadProgress.IsIndeterminate = false;
            foreach (var action in _modelActions) action.IsEnabled = enabled[action];
            foreach (var button in _downloadButtons) button.IsEnabled = true;
            active.Content = _downloadLocalize("Download");
            if (model is not null) installed(model);
            RefreshRemoveButtons();
            if (_closeAfterDownload) { _closeAfterDownload = false; Close(); }
        }
    }
}
