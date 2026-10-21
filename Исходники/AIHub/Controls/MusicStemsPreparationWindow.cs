using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Controls;

/// <summary>Standard verify/download interaction; successful preparation closes automatically.</summary>
public sealed class MusicStemsPreparationWindow : Window
{
    private readonly CancellationTokenSource _cancel;
    private readonly TextBlock _status = MusicWishUi.Text("");
    private readonly System.Windows.Controls.ProgressBar _progress = new() { Height = 8, Margin = new(0, 12, 0, 12) };
    private readonly System.Windows.Controls.Button _verify, _download;
    private readonly Func<string, string> _l;
    private bool _busy, _closing;
    public MusicStemsPreparationWindow(Window? owner, Func<string, string> localize, CancellationToken token)
    {
        _l = localize; _cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        MusicWishUi.PrepareWindow(this, _l("Music.Stems.Title"), "Music.Stems.Preparation", 700); Height = 380;
        if (owner is not null) Owner = owner;
        var root = new StackPanel { Margin = new(22) }; root.Children.Add(MusicWishUi.Text(_l("Music.Stems.EnableHint")));
        root.Children.Add(_status); root.Children.Add(_progress);
        var actions = new WrapPanel();
        _verify = MusicWishUi.Button(_l("Music.Verify"), "Music.Stems.Verify", () => _ = RunAsync(false));
        _download = MusicWishUi.Button(_l("Music.Download"), "Music.Stems.Download", () => _ = RunAsync(true));
        actions.Children.Add(_verify); actions.Children.Add(_download);
        actions.Children.Add(MusicWishUi.Button(_l("Music.Lora.Cancel"), "Music.Stems.Cancel", Close)); root.Children.Add(actions); Content = root;
        Loaded += (_, _) => _ = RunAsync(false);
        Closing += (_, e) => { if (_busy) { e.Cancel = true; _closing = true; _cancel.Cancel(); } };
        Closed += (_, _) => { _cancel.Cancel(); _cancel.Dispose(); };
    }
    private async Task RunAsync(bool download)
    {
        if (_busy) return; _busy = true; _download.IsEnabled = _verify.IsEnabled = false; _progress.IsIndeterminate = true;
        try {
            await MusicStemsCatalog.PrepareAsync(download, new Progress<ComponentDownloadProgress>(p => {
                _status.Text = p.ComponentId + " · " + p.Stage; _progress.IsIndeterminate = p.TotalBytes <= 0;
                if (p.TotalBytes > 0) _progress.Value = 100d * p.DownloadedBytes / p.TotalBytes;
            }), _cancel.Token);
            _busy = false; DialogResult = true;
        }
        catch (OperationCanceledException) { _status.Text = _l("Music.Lora.Cancelled"); }
        catch (Exception error) { _status.Text = _l("Music.Preparation.Missing") + "\n" + error.Message; }
        finally { _busy = false; _download.IsEnabled = _verify.IsEnabled = true; _progress.IsIndeterminate = false; if (_closing) Close(); }
    }
    public static async Task<bool> EnsureAsync(FrameworkElement view, Func<string, string> l, CancellationToken token)
    {
        try { await MusicStemsCatalog.PrepareAsync(false, null, token); return true; }
        catch (OperationCanceledException) { return false; }
        catch (IOException) { return new MusicStemsPreparationWindow(Window.GetWindow(view), l, token).ShowDialog() == true; }
    }
}
