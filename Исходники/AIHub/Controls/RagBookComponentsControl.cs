using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using UserControl = System.Windows.Controls.UserControl;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace AIHub.Controls;

public sealed class RagBookComponentsControl : UserControl, IDisposable
{
    private readonly Func<string, string> _l;
    private readonly Dictionary<string, string> _labels = [];
    private readonly Dictionary<string, TextBlock> _folders = [];
    private readonly RagBookModelPreparation _preparation;
    private readonly Dictionary<string, TextBlock> _states = [];
    private readonly List<Button> _actions = [];
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly System.Windows.Controls.ProgressBar _progress = new() { Height = 8, Maximum = 100 };
    private readonly Button _pause, _cancel;
    private readonly BackgroundOperationController? _background;
    private readonly RagBookAcquisitionWork _work;
    private int _connections;
    public string ModelsRoot { get; }
    public bool IsWorking => _operation is not null;
    public event Action? BackRequested;
    private CancellationTokenSource? _operation;
    private bool _disposed, _canceling;
    private string? _errorDetail;

    public RagBookComponentsControl(Func<string, string> localize, string modelsRoot,
        int connections, Action showLicenses, RagBookAcquisitionWork? work = null)
    {
        _l = localize; ModelsRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modelsRoot));
        _preparation = new(ModelsRoot, connections); _connections = connections;
        _background = ApplicationBackgroundOperations.Current; _work = work ?? new(_background);
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/RagBookResources.xaml", UriKind.Relative) });
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        SetResourceReference(FontSizeProperty, "UiBodyFontSize");
        AutomationProperties.SetAutomationId(this, "RagBooks.ComponentsPage");
        var root = new DockPanel { Margin = new Thickness(24) };
        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(_status); footer.Children.Add(_progress);
        var controls = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        Button Add(string key, Action action, string id)
        {
            var b = RagBookWorkspaceControl.ActionButton(T(key), action, "RagBooks." + id);
            controls.Children.Add(b); return b;
        }
        _actions.Add(Add("DownloadRequired", () => _ = RunAsync([RagBookModels.EmbeddingId, RagBookModels.AssistantId], true), "DownloadRequired"));
        _actions.Add(Add("CheckFiles", () => _ = RunAsync(_preparation.Cards.Select(c => c.ModelArtifactId).ToArray(), false), "CheckFiles"));
        _actions.Add(Add("Licenses", showLicenses, "ComponentLicenses"));
        _pause = Add("PauseDownload", () => _ = ChangePauseAsync(), "PauseDownload"); _pause.IsEnabled = false;
        _cancel = Add("CancelDownload", () => _ = CancelAsync(), "CancelDownload"); _cancel.IsEnabled = false;
        Add("Back", () => BackRequested?.Invoke(), "ComponentsBack"); footer.Children.Add(controls);

        var body = new StackPanel(); body.Children.Add(RagBookWorkspaceControl.Text(T("Components"), true));
        body.Children.Add(RagBookWorkspaceControl.Text(T("ComponentHint")));
        body.Children.Add(RagBookWorkspaceControl.Text(string.Format(T("RequiredSize"),
            Size(_preparation.Cards.Where(c => c.ModelArtifactId != ManagedModelCatalog.CoreArtifactId).Sum(c => c.TotalBytes)))));
        foreach (var card in _preparation.Cards)
        {
            var panel = new StackPanel();
            panel.Children.Add(RagBookWorkspaceControl.Text(card.DisplayName, true));
            var role = card.ModelArtifactId == RagBookModels.EmbeddingId ? "EmbeddingRole"
                : card.ModelArtifactId == RagBookModels.AssistantId ? "AssistantRole" : "FallbackRole";
            panel.Children.Add(RagBookWorkspaceControl.Text(T(role)));
            panel.Children.Add(RagBookWorkspaceControl.Text($"{Size(card.TotalBytes)} · {card.License}"));
            var folder = RagBookWorkspaceControl.Text(string.Format(T("ModelFolder"), card.InstallDirectory));
            _folders.Add(card.ModelArtifactId, folder); panel.Children.Add(folder);
            var state = RagBookWorkspaceControl.Text(T(Present(card) ? "FilesFound" : "FilesMissing"));
            AutomationProperties.SetAutomationId(state, "RagBooks.State." + card.ModelArtifactId);
            _states.Add(card.ModelArtifactId, state); panel.Children.Add(state);
            var b = RagBookWorkspaceControl.ActionButton(T("DownloadOrResume"),
                () => _ = RunAsync([card.ModelArtifactId], true), "RagBooks.Download." + card.ModelArtifactId);
            panel.Children.Add(b); _actions.Add(b);
            var surface = RagBookWorkspaceControl.Surface(panel); surface.Margin = new Thickness(0, 8, 0, 8); body.Children.Add(surface);
        }
        body.Children.Add(RagBookWorkspaceControl.Text(T("RuntimeLater")));
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        _status.Text = T("NoAutomaticDownload"); Content = root;
        if (_background is not null) _background.Changed += BackgroundChanged;
        RefreshOperationState();
    }

    private string T(string key) { var value = _l("RagBooks." + key); _labels.TryAdd(key, value); return value; }
    public void RefreshLocalization()
    {
        RagBookUiLocalization.Refresh(this, _labels, _l);
        foreach (var card in _preparation.Cards) _folders[card.ModelArtifactId].Text = string.Format(T("ModelFolder"), card.InstallDirectory);
        RefreshOperationState();
    }
    public void ConfigureConnections(int connections) => _connections = connections;
    private static bool Present(ManagedModelArtifactCard card)
    {
        try { return RagBookModelPreparation.FilesPresent(card); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    public Task ResumeAsync(BackgroundOperationState state, CancellationToken token)
    {
        var request = RagBookAcquisitionWork.Read(state);
        return RunAsync(request.Ids, request.Download, state, token);
    }

    private BackgroundOperationState? OwnState => _background?.FindOperation(RagBookAcquisitionWork.Kind) is { } state
        && string.Equals(state.Project, ModelsRoot, StringComparison.OrdinalIgnoreCase) ? state : null;

    private void BackgroundChanged()
    {
        if (!_disposed && !Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(RefreshOperationState);
    }

    public void RefreshOperationState()
    {
        if (_disposed) return;
        var state = OwnState;
        var pending = state is not null && state.Phase is not (BackgroundOperationPhase.Completed or BackgroundOperationPhase.Failed or BackgroundOperationPhase.Canceled);
        var blocked = _background?.BlocksNewWork == true;
        foreach (var b in _actions) b.IsEnabled = _operation is null && !blocked && !pending;
        _pause.IsEnabled = pending && state!.Phase != BackgroundOperationPhase.Pausing && !_canceling;
        _cancel.IsEnabled = pending && !_canceling;
        _pause.Content = T(state?.Phase is BackgroundOperationPhase.Paused or BackgroundOperationPhase.Waiting or BackgroundOperationPhase.Countdown ? "ResumeDownload" : "PauseDownload");
        AutomationProperties.SetName(_pause, _pause.Content.ToString() ?? "");
        if (state?.Phase is BackgroundOperationPhase.Paused or BackgroundOperationPhase.Waiting or BackgroundOperationPhase.Countdown or BackgroundOperationPhase.Pausing)
        { _progress.IsIndeterminate = false; _status.Text = state.RequiresDecision && !string.IsNullOrEmpty(_errorDetail) ? _errorDetail : T("State." + state.Phase); }
        else if (state?.Phase == BackgroundOperationPhase.Completed && _operation is null) _status.Text = T("CheckComplete");
        else if (blocked && state is null) _status.Text = T("OtherOperation");
    }

    private async Task RunAsync(string[] ids, bool download, BackgroundOperationState? restored = null, CancellationToken token = default)
    {
        if (_operation is not null || _disposed) return;
        var operation = CancellationTokenSource.CreateLinkedTokenSource(token, ApplicationBackgroundOperations.ExitToken);
        _operation = operation; _canceling = false; _errorDetail = null;
        foreach (var b in _actions) b.IsEnabled = false;
        _progress.Value = 0; _progress.IsIndeterminate = true;
        var progress = new Progress<ManagedModelDownloadProgress>(p =>
        {
            if (!ReferenceEquals(_operation, operation) || _disposed) return;
            if (OwnState?.Phase is BackgroundOperationPhase.Paused or BackgroundOperationPhase.Waiting) return;
            _progress.IsIndeterminate = p.TotalBytes <= 0 || p.Stage.StartsWith("verif", StringComparison.Ordinal);
            if (p.TotalBytes > 0) _progress.Value = Math.Clamp(100d * p.DownloadedBytes / p.TotalBytes, 0, 100);
            if (_states.TryGetValue(p.ModelArtifactId, out var state)) state.Text = T(p.Stage.StartsWith("verif", StringComparison.Ordinal) ? "Verifying" : "Downloading");
            _status.Text = T(p.Stage.StartsWith("verif", StringComparison.Ordinal) ? "Verifying" : "Downloading")
                + $" · {p.FileName} · {Size(p.DownloadedBytes)} / {Size(p.TotalBytes)}";
        });
        try
        {
            var cards = await _work.RunAsync(new(ModelsRoot, _connections, ids, download), T("AcquisitionTitle"), progress, operation.Token, restored);
            operation.Token.ThrowIfCancellationRequested();
            foreach (var card in cards) _states[card.ModelArtifactId].Text = T(card.Status == ManagedModelStatuses.Installed ? "FilesVerified" : "FilesMissing");
            _status.Text = T("CheckComplete");
        }
        catch (OperationCanceledException) { _status.Text = T(_canceling ? "DownloadCanceled" : "DownloadPaused"); }
        catch (Exception e)
        {
            _status.Text = e is BackgroundOperationWaitingException waiting ? _l(waiting.Message) : T("DownloadError") + " " + e.Message;
            _errorDetail = _status.Text;
            OwnedProcessRegistry.Log("rag_book_acquisition_failed", "RagBooks", detail: e.ToString());
        }
        finally
        {
            operation.Dispose(); _operation = null;
            _progress.IsIndeterminate = false;
            RefreshOperationState();
            if (_disposed) _preparation.Dispose();
        }
    }

    private async Task ChangePauseAsync()
    {
        try
        {
            if (_background is null) { _operation?.Cancel(); return; }
            if (OwnState?.Phase == BackgroundOperationPhase.Running) await _background.PauseAsync();
            else if (OwnState is not null) await _background.ResumeAsync(OwnState.Id, ApplicationBackgroundOperations.ExitToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { _status.Text = T("DownloadError") + " " + e.Message; }
        finally { RefreshOperationState(); }
    }

    private async Task CancelAsync()
    {
        var id = OwnState?.Id; if (id is null) return;
        _canceling = true; RefreshOperationState(); _operation?.Cancel();
        try
        {
            if (_background is not null)
            {
                await _background.WaitForIdleAsync(id);
                _background.DiscardPending(id);
            }
            _status.Text = T("DownloadCanceled");
        }
        catch (Exception e) { _status.Text = T("DownloadError") + " " + e.Message; }
        finally { _canceling = false; RefreshOperationState(); }
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        if (_background is not null) _background.Changed -= BackgroundChanged;
        _operation?.Cancel(); if (_operation is null) _preparation.Dispose();
    }

    internal static string Size(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double amount = Math.Max(0, bytes); var unit = 0;
        while (amount >= 1000 && unit < units.Length - 1) { amount /= 1000; ++unit; }
        return $"{amount:0.##} {units[unit]}";
    }
}
