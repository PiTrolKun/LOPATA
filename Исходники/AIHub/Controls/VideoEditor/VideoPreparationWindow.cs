using System.Windows;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VideoEditor;
using Button = System.Windows.Controls.Button;

namespace AIHub.Controls.VideoEditor;

/// <summary>Standard verify/download/continue dialog. It never starts the caller after cancellation.</summary>
internal sealed class VideoPreparationWindow : Window
{
    private readonly Func<bool, IProgress<ManagedModelDownloadProgress>, CancellationToken, Task<bool>> _prepare;
    private readonly Func<string, string> _l;
    private readonly CancellationTokenSource _cancel;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 12) };
    private readonly System.Windows.Controls.ProgressBar _progress = new() { Height = 7, Margin = new(0, 10, 0, 10) };
    private readonly Button _verify, _download;
    private bool _busy, _closing;
    internal VideoPreparationWindow(Window owner, Func<string, string> localize, string name,
        Func<bool, IProgress<ManagedModelDownloadProgress>, CancellationToken, Task<bool>> prepare, CancellationToken token, string? details = null)
    {
        _prepare = prepare; _l = localize; _cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        MusicWishUi.PrepareWindow(this, T("ModelPreparation"), "VideoEditor.Preparation", 760); Height = 380; MinHeight = 300;
        Owner = owner; Resources.MergedDictionaries.Insert(0, owner.Resources);
        var panel = new StackPanel { Margin = new(24) }; panel.Children.Add(MusicWishUi.Text(name, true)); panel.Children.Add(MusicWishUi.Text(T("PreparationHint")));
        if (details is not null)
        {
            Height = 650;
            panel.Children.Add(new ScrollViewer { Content = MusicWishUi.Text(details), Height = 245,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        }
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        panel.Children.Add(_status); panel.Children.Add(_progress);
        var actions = new WrapPanel();
        _verify = MusicWishUi.Button(T("VerifyModels"), "VideoEditor.Preparation.Verify", () => _ = ExecuteAsync(false));
        _download = MusicWishUi.Button(T("DownloadModels"), "VideoEditor.Preparation.Download", () => _ = ExecuteAsync(true));
        actions.Children.Add(_verify); actions.Children.Add(_download); actions.Children.Add(MusicWishUi.Button(T("CancelPreparation"), "VideoEditor.Preparation.Cancel", Close)); panel.Children.Add(actions);
        Content = details is null ? panel : new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Loaded += (_, _) => _ = ExecuteAsync(false);
        Closing += (_, e) => { if (_busy) { e.Cancel = true; _closing = true; _cancel.Cancel(); } };
        Closed += (_, _) => { _cancel.Cancel(); _cancel.Dispose(); };
    }
    private string T(string key) => _l("VideoEditor." + key);
    internal async Task ExecuteAsync(bool download)
    {
        if (_busy) return; _busy = true; _verify.IsEnabled = _download.IsEnabled = false; _progress.IsIndeterminate = true;
        try {
            var ready = await _prepare(download, new Progress<ManagedModelDownloadProgress>(p => {
                if (_closing) return; _status.Text = p.FileName + " · " + p.Stage; _progress.IsIndeterminate = p.TotalBytes <= 0;
                if (p.TotalBytes > 0) _progress.Value = 100d * p.DownloadedBytes / p.TotalBytes;
            }), _cancel.Token);
            _cancel.Token.ThrowIfCancellationRequested();
            if (ready) { _busy = false; DialogResult = true; return; }
            _status.Text = T("ModelsMissing");
        }
        catch (OperationCanceledException) { _status.Text = T("Interrupted"); }
        catch (Exception error) { VideoErrors.Report(error); _status.Text = T("PreparationFailed") + "\n" + (error.Message.StartsWith("VideoEditor.", StringComparison.Ordinal) ? _l(error.Message) : error.Message); }
        finally { _busy = false; _progress.IsIndeterminate = false; _verify.IsEnabled = _download.IsEnabled = true; if (_closing) Close(); }
    }
}
