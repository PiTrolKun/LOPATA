using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services.MusicLora;

namespace AIHub.Controls;

public sealed class MusicLoraPreparationWindow : Window
{
    private readonly CancellationTokenSource _cancel;
    private readonly string _root;
    private readonly MusicLoraUse[] _uses;
    private readonly Func<string, string> _l;
    private readonly TextBlock _status = MusicWishUi.Text("");
    private readonly System.Windows.Controls.ProgressBar _progress = new() { Height = 8, Margin = new(0, 12, 0, 12) };
    private readonly System.Windows.Controls.Button _download, _verify;
    private bool _busy, _closing;
    public MusicLoraPreparationWindow(Window? owner, string root, MusicLoraUse[] uses, Func<string, string> localize, CancellationToken token)
    {
        _root = root; _uses = uses; _l = localize; _cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        MusicWishUi.PrepareWindow(this, T("Preparation"), "Music.Lora.Preparation", 700); Height = 360;
        if (owner is not null) Owner = owner;
        var panel = new StackPanel { Margin = new(22) }; panel.Children.Add(MusicWishUi.Text(T("PreparationHint")));
        panel.Children.Add(_status); panel.Children.Add(_progress);
        var actions = new WrapPanel();
        _verify = MusicWishUi.Button(T("Verify"), "Music.Lora.Verify", () => _ = RunAsync(false));
        _download = MusicWishUi.Button(T("Download"), "Music.Lora.Download", () => _ = RunAsync(true));
        actions.Children.Add(_verify); actions.Children.Add(_download);
        actions.Children.Add(MusicWishUi.Button(T("Cancel"), "Music.Lora.Cancel", Close)); panel.Children.Add(actions); Content = panel;
        Loaded += (_, _) => _ = RunAsync(false); Closing += OnClosing; Closed += (_, _) => { _cancel.Cancel(); _cancel.Dispose(); };
    }
    private string T(string key) => _l("Music.Lora." + key);
    private async Task RunAsync(bool download)
    {
        if (_busy) return; _busy = true; _verify.IsEnabled = _download.IsEnabled = false; _progress.IsIndeterminate = true;
        try {
            var ready = await MusicLoraAssets.PrepareAsync(_root, _uses, download, new Progress<ManagedModelDownloadProgress>(p => {
                _status.Text = p.FileName + " · " + p.Stage; _progress.IsIndeterminate = p.TotalBytes <= 0;
                _progress.Value = p.TotalBytes <= 0 ? 0 : 100d * p.DownloadedBytes / p.TotalBytes;
            }), _cancel.Token);
            if (ready) { _busy = false; DialogResult = true; return; } _status.Text = T("Missing");
        } catch (OperationCanceledException) { _status.Text = T("Cancelled"); }
        catch (Exception e) { _status.Text = T("Failed") + "\n" + e.Message; }
        finally { _busy = false; _progress.IsIndeterminate = false; _verify.IsEnabled = _download.IsEnabled = true; if (_closing) Close(); }
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    { if (_busy) { e.Cancel = true; _closing = true; _cancel.Cancel(); } }
}
