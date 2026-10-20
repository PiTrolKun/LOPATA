using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using AIHub.Services.MusicLibrary;
using Button = System.Windows.Controls.Button;

namespace AIHub.Controls;

/// <summary>Standard verification/acquisition flow; existing gates own consent and downloads.</summary>
public sealed class LyricLibraryPreparationWindow : Window
{
    private readonly CancellationTokenSource _cancel;
    private readonly string _root;
    private readonly Func<string, string> _l;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 12) };
    private readonly Button _verify, _download;
    private readonly System.Windows.Controls.ProgressBar _progress = new() { Height = 7, Margin = new(0, 10, 0, 10) };
    private bool _busy, _closing;
    internal Func<string, bool, IProgress<string>, CancellationToken, Task<bool>> Prepare { get; set; }
        = LyricLibraryPreparation.PrepareAsync;
    public LyricLibraryPreparationWindow(Window? owner, string root, Func<string, string> localize, CancellationToken token)
    {
        _root = root; _l = localize; _cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        MusicWishUi.PrepareWindow(this, T("Preparation"), "Music.Library.Preparation", 760);
        Height = 380; MinHeight = 300;
        if (owner is not null) { Owner = owner; Resources.MergedDictionaries.Insert(0, owner.Resources); }
        var panel = new StackPanel { Margin = new(24) };
        panel.Children.Add(MusicWishUi.Text(T("Preparation"), true));
        panel.Children.Add(MusicWishUi.Text(T("PreparationHint"))); panel.Children.Add(_status); panel.Children.Add(_progress);
        var actions = new WrapPanel();
        _verify = MusicWishUi.Button(T("Verify"), "Music.Library.Verify", () => _ = RunAsync(false));
        _download = MusicWishUi.Button(T("Download"), "Music.Library.Download", () => _ = RunAsync(true));
        actions.Children.Add(_verify); actions.Children.Add(_download);
        actions.Children.Add(MusicWishUi.Button(T("Cancel"), "Music.Library.Cancel", Close)); panel.Children.Add(actions);
        Content = panel; Loaded += (_, _) => _ = RunAsync(false); Closing += OnClosing;
        Closed += (_, _) => { _cancel.Cancel(); _cancel.Dispose(); };
    }
    private string T(string key) => _l("Music.Library." + key);
    private async Task RunAsync(bool download)
    {
        if (_busy) return; _busy = true; _verify.IsEnabled = _download.IsEnabled = false; _progress.IsIndeterminate = true;
        try
        {
            var ready = await Prepare(_root, download,
                new Progress<string>(s => { if (!_closing) _status.Text = s; }), _cancel.Token);
            _cancel.Token.ThrowIfCancellationRequested();
            if (ready) { _busy = false; DialogResult = true; return; }
            _status.Text = T("Missing");
        }
        catch (OperationCanceledException) { _status.Text = T("Cancelled"); }
        catch (Exception error) { _status.Text = T("PreparationFailed") + "\n" + error.Message; }
        finally
        {
            _busy = false; _progress.IsIndeterminate = false; _verify.IsEnabled = _download.IsEnabled = true;
            if (_closing) Close();
        }
    }
    private void OnClosing(object? sender, CancelEventArgs args)
    { if (_busy) { args.Cancel = true; _closing = true; _cancel.Cancel(); } }
}
