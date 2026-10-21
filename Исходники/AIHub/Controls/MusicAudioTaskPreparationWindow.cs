using System.IO;
using System.Windows;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Controls;

public sealed class MusicAudioTaskPreparationWindow : Window
{
    private readonly CancellationTokenSource _cancel;
    private readonly MusicAudioTask _task;
    private readonly string _variation;
    private readonly bool _recognizer;
    private readonly Func<string, string> _l;
    private readonly TextBlock _status = MusicWishUi.Text("");
    private readonly System.Windows.Controls.ProgressBar _progress = new() { Height = 8, Margin = new(0, 12, 0, 12) };
    private readonly System.Windows.Controls.Button _verify, _download;
    private bool _busy, _closing;
    public MusicAudioTaskPreparationWindow(Window? owner, MusicAudioTask task, string variation, Func<string, string> l, CancellationToken token, bool recognizer = false)
    {
        _recognizer = recognizer;
        _task = task; _variation = variation; _l = l; _cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        MusicWishUi.PrepareWindow(this, l("Music.Transform.Preparation"), "Music.Transform.Preparation", 720); Height = 380;
        if (owner is not null) Owner = owner;
        var root = new StackPanel { Margin = new(22) }; root.Children.Add(MusicWishUi.Text(l("Music.Transform.PreparationHint")));
        root.Children.Add(_status); root.Children.Add(_progress); var actions = new WrapPanel();
        _verify = MusicWishUi.Button(l("Music.Verify"), "Music.Transform.Verify", () => _ = RunAsync(false));
        _download = MusicWishUi.Button(l("Music.Download"), "Music.Transform.Download", () => _ = RunAsync(true));
        actions.Children.Add(_verify); actions.Children.Add(_download);
        actions.Children.Add(MusicWishUi.Button(l("Music.Lora.Cancel"), "Music.Transform.Cancel", Close)); root.Children.Add(actions); Content = root;
        Loaded += (_, _) => _ = RunAsync(false);
        Closing += (_, e) => { if (_busy) { e.Cancel = true; _closing = true; _cancel.Cancel(); } };
        Closed += (_, _) => { _cancel.Cancel(); _cancel.Dispose(); };
    }
    internal static async Task PrepareAsync(MusicAudioTask task, string variation, bool download, IProgress<ComponentDownloadProgress>? progress, CancellationToken token)
    {
        if (!task.Enabled) return; task.Validate(variation);
        await MusicAudioRuntime.Default.PrepareAsync(token);
        if (task.UsesGenerator && variation == MusicStudioRuntime.Variation) await MusicCoverScoreCatalog.PrepareAsync(download, progress, token);
        if (task.NeedsSeparation || MusicAudioInputPolicy.ActiveReferences(task, variation).Any(r => r.Role == MusicReferenceRole.Voice)) await MusicStemsCatalog.PrepareAsync(download, progress, token);
        if (task.NeedsSeparation) {
            await MusicLyricsComponentCatalog.PrepareAsync(download, progress, token);
        }
    }
    private async Task RunAsync(bool download)
    {
        if (_busy) return; _busy = true; _verify.IsEnabled = _download.IsEnabled = false; _progress.IsIndeterminate = true;
        try {
            var progress = new Progress<ComponentDownloadProgress>(p => {
                _status.Text = p.ComponentId + " · " + p.Stage; _progress.IsIndeterminate = p.TotalBytes <= 0;
                if (p.TotalBytes > 0) _progress.Value = 100d * p.DownloadedBytes / p.TotalBytes;
            });
            if (_recognizer) await MusicSourceLyrics.PrepareAsync(download, progress, _cancel.Token);
            else await PrepareAsync(_task, _variation, download, progress, _cancel.Token);
            _busy = false; DialogResult = true;
        } catch (OperationCanceledException) { _status.Text = _l("Music.Lora.Cancelled"); }
        catch (Exception e) { _status.Text = _l("Music.Preparation.Missing") + "\n" + (e.Message.StartsWith("Music.") ? _l(e.Message) : e.Message); }
        finally { _busy = false; _verify.IsEnabled = _download.IsEnabled = true; _progress.IsIndeterminate = false; if (_closing) Close(); }
    }
    public static async Task<bool> EnsureRecognizerAsync(Window owner, Func<string, string> l, CancellationToken token)
    {
        try { await MusicSourceLyrics.PrepareAsync(false, null, token); return true; }
        catch (OperationCanceledException) { return false; }
        catch (IOException) { return new MusicAudioTaskPreparationWindow(owner, new(), MusicAceCatalog.Variation, l, token, true).ShowDialog() == true; }
    }
    public static async Task<bool> EnsureAsync(FrameworkElement view, MusicAudioTask? task, string variation, Func<string, string> l, CancellationToken token)
    {
        if (task is not { Enabled: true }) return true;
        try { await PrepareAsync(task, variation, false, null, token); return true; }
        catch (OperationCanceledException) { return false; }
        catch (IOException) { return new MusicAudioTaskPreparationWindow(Window.GetWindow(view), task, variation, l, token).ShowDialog() == true; }
    }
}
