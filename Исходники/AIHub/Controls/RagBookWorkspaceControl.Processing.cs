using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using ProgressBar = System.Windows.Controls.ProgressBar;

namespace AIHub.Controls;

public sealed partial class RagBookWorkspaceControl
{
    private readonly Button _start = new(), _pauseProcessing = new(), _cancelProcessing = new(), _find = new();
    private readonly ProgressBar _processingProgress = new() { Height = 8, Maximum = 100, Margin = new Thickness(0, 12, 0, 16) };
    private readonly TextBox _journal = ReadOnlyBox(), _hits = ReadOnlyBox(), _query = new() { Margin = new Thickness(0, 4, 0, 8) };
    private readonly TextBlock _resultDescription = Text("");
    private readonly System.Windows.Controls.TabControl _processingTabs = new();
    private CancellationTokenSource? _processing;
    private bool _processingDisposed;
    private string? _library;
    private static TextBox ReadOnlyBox() => new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 180, MaxHeight = 450 };
    private BackgroundOperationState? ProcessingState => ApplicationBackgroundOperations.Current?.FindOperation(RagBookPreparation.Kind);

    private void InitializeProcessing()
    {
        if (ApplicationBackgroundOperations.Current is { } background) background.Changed += ProcessingChanged;
        if (ProcessingState is { } state) _library = RagBookPreparation.Read(state).Directory;
        else if (_canSave)
        {
            try { _library = RagBookPreparation.LibraryDirectory(_name.Text, _output.Text); }
            catch (Exception error) when (error is IOException or ArgumentException or InvalidDataException) { _status.Text = ErrorMessage(error); }
        }
        RefreshProcessingState(); ShowLibrary(true);
    }
    private void ProcessingChanged()
    {
        if (!_processingDisposed && !Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(RefreshProcessingState);
    }
    private void RefreshProcessingState()
    {
        if (_processingDisposed) return;
        var controller = ApplicationBackgroundOperations.Current; var state = ProcessingState;
        var pending = state is not null && state.Phase is not (BackgroundOperationPhase.Completed or BackgroundOperationPhase.Failed or BackgroundOperationPhase.Canceled);
        var busy = _processing is not null || controller?.BlocksNewWork == true;
        _start.IsEnabled = !busy && !pending && _books.Count > 0 && _canSave;
        _pauseProcessing.IsEnabled = pending && state!.Phase != BackgroundOperationPhase.Pausing;
        _cancelProcessing.IsEnabled = pending || _processing is not null;
        _pauseProcessing.Content = T(state?.Phase is BackgroundOperationPhase.Paused or BackgroundOperationPhase.Waiting or BackgroundOperationPhase.Countdown ? "ResumeProcessing" : "Pause");
        _name.IsEnabled = !busy && !pending; _list.IsEnabled = !busy && !pending;
        _cpuTarget.IsEnabled = !busy && !pending;
        foreach (var action in _sourceActions) action.IsEnabled = !busy && !pending;
        _remove.IsEnabled = !busy && !pending && _books.Count > 0;
        _searchLayer.IsEnabled = !busy;
        _find.IsEnabled = !busy && _library is not null && (LexiconSearch ? RagLexiconReader.Exists(_library) : File.Exists(Path.Combine(_library, "current.json")));
        _query.IsEnabled = _find.IsEnabled;
        if (pending) _status.Text = T("State." + state!.Phase);
        _processingProgress.IsIndeterminate = pending && state!.Phase == BackgroundOperationPhase.Running && _processingProgress.Value == 0;
    }
    public Task ResumePreparationAsync(BackgroundOperationState state, CancellationToken token)
        => StartProcessingAsync(RagBookPreparation.Read(state), state, token);

    private async Task StartProcessingAsync(RagBookJob? saved = null, BackgroundOperationState? restored = null, CancellationToken token = default)
    {
        if (_processing is not null || _processingDisposed || !SaveForExit()) return;
        if (saved is not null)
        {
            _name.Text = saved.Name; _output.Text = Path.GetDirectoryName(saved.Directory)!;
            _cpuTarget.SelectedItem = new[] { 25, 50, 75, 90 }.Contains(saved.CpuTargetPercent) ? saved.CpuTargetPercent : 50;
            _books.Clear(); _books.AddRange(saved.Books); RefreshBooks();
            if (!SaveForExit()) return;
        }
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token, ApplicationBackgroundOperations.ExitToken);
        _processing = operation; _processingProgress.Value = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var job = saved ?? RagBookPreparation.Create(_name.Text, _output.Text, _modelsRoot, _books) with
                { CpuTargetPercent = _cpuTarget.SelectedItem is int target ? target : 50 }; _library = job.Directory;
            var progress = new Progress<RagBookProgress>(p =>
            {
                if (_processingDisposed || !ReferenceEquals(_processing, operation)) return;
                if (p.Stage == "Load")
                {
                    _journal.AppendText(DateTime.Now.ToString("HH:mm:ss") + " · " + string.Format(T("LoadStatus"), p.Detail, job.CpuTargetPercent, p.Done, p.Total) + "\n");
                    if (_journal.Text.Length > 24000) _journal.Text = _journal.Text[^18000..];
                    return;
                }
                if (p.Stage == "Resource")
                {
                    _journal.AppendText(DateTime.Now.ToString("HH:mm:ss") + " · " + T("Resource") + " · " + p.Detail + "\n");
                    if (_journal.Text.Length > 24000) _journal.Text = _journal.Text[^18000..];
                    return;
                }
                _status.Text = T(p.Stage) + (p.Total > 0 ? $" · {p.Done}/{p.Total}" : "") + " · " + p.Detail;
                _processingProgress.IsIndeterminate = p.Total <= 0;
                if (p.Total > 0) _processingProgress.Value = 100d * p.Done / p.Total;
                _journal.AppendText(DateTime.Now.ToString("HH:mm:ss") + " · " + _status.Text + "\n");
                if (_journal.Text.Length > 24000) _journal.Text = _journal.Text[^18000..];
                _journal.ScrollToEnd();
            });
            RefreshProcessingState();
            await new RagBookPreparation().RunAsync(job, T("PreparationTitle"), progress, operation.Token, restored);
            _status.Text = T("Ready") + $" · {clock.Elapsed.TotalSeconds:0.0} s";
            _processingProgress.Value = 100; ShowLibrary(true);
        }
        catch (OperationCanceledException) { _status.Text = T("StoppedCheckpoint"); }
        catch (Exception error)
        {
            _status.Text = ErrorMessage(error); _journal.AppendText(_status.Text + "\n");
            OwnedProcessRegistry.Log("rag_book_preparation_failed", "RagBooks", detail: error.ToString());
        }
        finally { _processing = null; _processingProgress.IsIndeterminate = false; RefreshProcessingState(); }
    }
    private string ErrorMessage(Exception error) => error.Message.StartsWith("RagBooks.", StringComparison.Ordinal)
        ? _l(error.Message) : T("ProcessingError") + " " + error.Message;
    private async Task PauseProcessingAsync()
    {
        try
        {
            if (ApplicationBackgroundOperations.Current is not { } controller) return;
            if (ProcessingState?.Phase == BackgroundOperationPhase.Running) await controller.PauseAsync();
            else _ = ResumeObservedAsync(controller);
        }
        catch (Exception error) { _status.Text = ErrorMessage(error); }
    }
    private async Task ResumeObservedAsync(BackgroundOperationController controller)
    {
        try { await controller.ResumeAsync(ProcessingState?.Id, ApplicationBackgroundOperations.ExitToken); }
        catch (OperationCanceledException) { }
        catch (Exception error) { _status.Text = ErrorMessage(error); }
    }
    private async Task CancelProcessingAsync()
    {
        var controller = ApplicationBackgroundOperations.Current; var id = ProcessingState?.Id;
        _processing?.Cancel();
        if (controller is not null && id is not null) { await controller.WaitForIdleAsync(id); controller.DiscardPending(id); }
        _status.Text = T("StoppedCheckpoint"); RefreshProcessingState();
    }
    private void ShowLibrary(bool reveal = false)
    {
        _resultDescription.Text = T("NoResult");
        if (_library is null) return;
        try
        {
            var hasBooks = File.Exists(Path.Combine(_library, "current.json"));
            if (hasBooks)
            {
            using var pointer = JsonDocument.Parse(File.ReadAllText(Path.Combine(_library, "current.json")));
            var version = pointer.RootElement.GetProperty("version").GetString();
            if (!Guid.TryParseExact(version, "N", out _)) throw new InvalidDataException("Invalid library version.");
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(_library, "versions", version!, "manifest.json")));
            _resultDescription.Text = string.Format(T("ResultSummary"), manifest.RootElement.GetProperty("chunks").GetInt32(),
                manifest.RootElement.GetProperty("excluded_units").GetInt32(), _library);
            if (manifest.RootElement.TryGetProperty("exclusion_reasons", out var reasons))
                _resultDescription.Text += "\n" + string.Join("\n", reasons.EnumerateObject().Select(reason => T("Quality." + reason.Name) + ": " + reason.Value.GetInt32()));
            }
            if (RagLexiconReader.Exists(_library))
            {
                _resultDescription.Text = (hasBooks ? _resultDescription.Text + "\n\n" : "") + LexiconSummary();
                if (!hasBooks) _searchLayer.SelectedIndex = 1;
            }
            if (reveal) _processingTabs.SelectedIndex = 2;
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException) { _resultDescription.Text = ErrorMessage(error); }
    }
    private async Task FindAsync()
    {
        if (_library is null || _processing is not null) return;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ApplicationBackgroundOperations.ExitToken);
        _processing = operation; RefreshProcessingState();
        try
        {
            using var lease = ApplicationBackgroundOperations.Current?.BeginAuxiliary();
            _status.Text = T("Searching");
            if (LexiconSearch) { await FindLexiconAsync(operation.Token); _status.Text = T("Ready"); return; }
            var json = await RagBookWorker.SearchAsync(_library, _modelsRoot, _query.Text,
                new Progress<RagBookProgress>(p => _status.Text = T(p.Stage) + " · " + p.Detail), operation.Token);
            using var doc = JsonDocument.Parse(json);
            _hits.Text = string.Join("\n\n", doc.RootElement.GetProperty("hits").EnumerateArray().Select(h =>
                $"{h.GetProperty("source").GetString()} · {T("Page")} {h.GetProperty("page").GetInt32()} · {h.GetProperty("score").GetDouble():0.000}\n{h.GetProperty("text").GetString()}"));
            _status.Text = T("Ready");
        }
        catch (OperationCanceledException) { _status.Text = T("StoppedCheckpoint"); }
        catch (Exception error) { _status.Text = ErrorMessage(error); }
        finally { _processing = null; RefreshProcessingState(); }
    }
    private void DisposeProcessing()
    {
        _processingDisposed = true;
        if (ApplicationBackgroundOperations.Current is { } background) background.Changed -= ProcessingChanged;
        _processing?.Cancel();
    }
}
