using System.Windows;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;

namespace AIHub.Controls;

public sealed partial class MusicPoetryModelWindow
{
    private readonly Func<MusicPoetryModelCandidate, long>? _remove;
    private readonly Func<MusicPoetryModelCandidate, bool>? _hasFiles;
    private readonly List<(MusicPoetryModelCandidate Candidate, Button Button)> _removeButtons = [];
    public bool IsRemoving { get; private set; }
    private void RefreshRemoveButtons()
    {
        foreach (var (candidate, button) in _removeButtons)
        {
            var present = _hasFiles?.Invoke(candidate) == true;
            button.Visibility = present ? Visibility.Visible : Visibility.Collapsed;
            button.IsEnabled = present && !IsDownloading && !IsRemoving;
        }
    }
    private void RemoveProfile(MusicPoetryModelCandidate candidate, Func<string, string> localize, Action removed)
    {
        if (IsDownloading || IsRemoving || _remove is null) return;
        var message = string.Format(_downloadLocalize("RemoveConfirm"), candidate.DisplayName ?? candidate.FileName,
            FormatDownloadSize(candidate.DownloadBytes));
        if (new ModelRemovalConfirmationDialog(this, localize("Music.Models.Remove.Title"), message,
            localize("Common.Cancel"), localize("Music.Models.Remove.Action")).ShowDialog() != true) return;
        IsRemoving = true;
        try
        {
            var bytes = _remove(candidate);
            removed();
            _downloadStatus.Text = string.Format(localize("Music.Models.Remove.Done"), FormatDownloadSize(bytes));
        }
        catch (Exception error)
        {
            OwnedProcessRegistry.Log("poetry_model_remove_failed", "Music.Poetry.Removal", detail: error.ToString());
            _downloadStatus.Text = _downloadLocalize("RemoveError") + " " + _localizeDownloadError(error.Message);
        }
        finally { IsRemoving = false; RefreshRemoveButtons(); }
    }
}
