using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using ProgressBar = System.Windows.Controls.ProgressBar;

namespace AIHub.Controls;

/// <summary>Explicit entry gate: checking and downloading never enter the conversation by themselves.</summary>
public sealed class LetItOutChatPreparationWindow : Window
{
    private readonly Func<string, string> _l;
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationTokenRegistration _exitRegistration;
    private CancellationTokenSource? _operation;
    private readonly TextBlock _core = MusicWishUi.Text(""), _runtime = MusicWishUi.Text(""),
        _size = MusicWishUi.Text(""), _status = MusicWishUi.Text(""), _repair = MusicWishUi.Text("");
    private readonly ProgressBar _progress = new() { Height = 8, Margin = new(0, 10, 0, 10) };
    private readonly Button _verify, _download, _next, _cancel;
    private bool _loaded, _closing, _closed, _accepted;
    private LetItOutChatReadiness? _readiness;
    public string? CoreModelPath { get; private set; }
    internal Func<bool, IProgress<LetItOutChatPreparationProgress>, CancellationToken, Task<LetItOutChatReadiness>> Prepare { get; set; }

    public LetItOutChatPreparationWindow(Window owner, StorageSettings storage, int connections,
        Func<string, string> l, CancellationToken token)
    {
        _l = l;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, ApplicationBackgroundOperations.ExitToken);
        Prepare = (download, progress, ct) => LetItOutChatPreparation.PrepareAsync(storage, connections, download, progress, ct);
        MusicWishUi.PrepareWindow(this, T("Title"), Id("Window"), 790);
        Owner = owner; Resources.MergedDictionaries.Insert(0, owner.Resources);
        Height = 510; MinWidth = 480; MinHeight = 380;
        var body = new DockPanel { Margin = new(24) };
        var actions = new WrapPanel { Margin = new(0, 12, 0, 0) };
        _verify = MusicWishUi.Button(T("Verify"), Id("Verify"), () => _ = RunPreparationAsync(false));
        _download = MusicWishUi.Button(T("Download"), Id("Download"), () => _ = RunPreparationAsync(true));
        _next = MusicWishUi.Button(T("Continue"), Id("Continue"), () => _ = ContinueAsync());
        _cancel = MusicWishUi.Button(T("Cancel"), Id("Cancel"), Close);
        foreach (var button in new[] { _verify, _download, _next, _cancel }) actions.Children.Add(button);
        DockPanel.SetDock(actions, Dock.Bottom); body.Children.Add(actions);
        var content = new StackPanel();
        content.Children.Add(MusicWishUi.Text(T("Title"), true));
        content.Children.Add(MusicWishUi.Text(T("Hint")));
        foreach (var text in new[] { _core, _runtime, _size, _repair, _status }) content.Children.Add(text);
        content.Children.Add(_progress);
        body.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = body;
        AutomationProperties.SetAutomationId(_status, Id("Status"));
        _core.Text = LetItOutModel.DisplayName;
        _runtime.Text = T("CheckingRuntime");
        _size.Text = string.Format(T("ModelSize"), Bytes(LetItOutModel.TotalBytes));
        RefreshButtons();
        Loaded += async (_, _) => { if (_loaded) return; _loaded = true; if (_lifetime.IsCancellationRequested) Close(); else await RunPreparationAsync(false); };
        Closing += OnClosing;
        Closed += (_, _) => { _closed = true; _exitRegistration.Dispose(); _lifetime.Dispose(); };
        _exitRegistration = _lifetime.Token.Register(() => Dispatcher.BeginInvoke(() => { if (!_closed) Close(); }));
    }

    private string T(string key) => _l("LetItOut.Preparation." + key);
    private static string Id(string key) => "LetItOut.Preparation." + key;
    private static string Bytes(long bytes) => ComponentCardViewModel.FormatBytes(bytes);

    internal async Task RunPreparationAsync(bool download)
    {
        if (_operation is not null || _closing || _closed) return;
        CoreModelPath = null; _readiness = null;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operation = operation;
        _progress.IsIndeterminate = true; _status.Text = T("CheckingCore");
        _core.Text = LetItOutModel.DisplayName + " — " + T("CheckingCore");
        _runtime.Text = T("CheckingRuntime");
        RefreshButtons();
        try
        {
            var progress = new Progress<LetItOutChatPreparationProgress>(p =>
            {
                if (_operation != operation || _closing || _closed) return;
                _status.Text = p.TotalBytes > 0
                    ? string.Format(T("Progress"), T(p.StageKey), Bytes(p.DownloadedBytes), Bytes(p.TotalBytes), Bytes((long)p.BytesPerSecond))
                    : T(p.StageKey);
                _progress.IsIndeterminate = p.TotalBytes <= 0;
                if (p.TotalBytes > 0) _progress.Value = Math.Clamp(p.DownloadedBytes * 100d / p.TotalBytes, 0, 100);
            });
            var result = await Prepare(download, progress, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            _readiness = result;
            _core.Text = LetItOutModel.DisplayName + " — " + T(result.CoreReady ? "Available" : "MissingComponent");
            _runtime.Text = T("Runtime") + " — " + T(result.RuntimeReady ? "Available" : "MissingComponent")
                + (result.RuntimeBackend is { } backend ? " · " + backend : "");
            _size.Text = string.Format(T("Size"), Bytes(result.RequiredBytes), Bytes(result.RemainingBytes));
            _repair.Text = result.RepairsEngineSet ? T("EngineSetRepair") : "";
            _status.Text = T(result.StatusKey);
        }
        catch (OperationCanceledException) { _status.Text = T("Cancelled"); }
        catch (Exception error)
        {
            ComponentAuditFailures.Report(error);
            OwnedProcessRegistry.Log("preparation_failed", "LetItOutChat", detail: error.GetType().Name);
            _status.Text = T("Failed");
        }
        finally
        {
            _operation = null; _progress.IsIndeterminate = false;
            _progress.Value = _readiness?.Ready == true ? 100 : 0;
            RefreshButtons();
            if (_closing && !_closed) Close();
        }
    }

    internal async Task ContinueAsync()
    {
        if (_operation is not null || _readiness?.Ready != true || _closing || _closed) return;
        await RunPreparationAsync(false);
        if (_closing || _closed || _lifetime.IsCancellationRequested || _readiness?.Ready != true) return;
        CoreModelPath = _readiness.CoreModelPath;
        _accepted = true;
        DialogResult = true;
    }

    private void RefreshButtons()
    {
        var idle = _operation is null && !_closing && !_closed;
        _verify.IsEnabled = idle;
        _download.IsEnabled = idle && _readiness is { Ready: false, CanDownload: true };
        _next.IsEnabled = idle && _readiness?.Ready == true;
        _cancel.IsEnabled = !_closing && !_closed;
    }

    private void OnClosing(object? sender, CancelEventArgs args)
    {
        if (!_accepted) CoreModelPath = null;
        if (_operation is null) return;
        args.Cancel = true; _closing = true;
        _operation.Cancel(); RefreshButtons(); _status.Text = T("Cancelling");
    }
}
