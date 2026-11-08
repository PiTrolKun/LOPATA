using System.IO;
using System.Windows;
using AIHub.Controls.VideoEditor;
using AIHub.Services;
using AIHub.Services.OfficeReader;
using AIHub.Services.VideoEditor;
using Button = System.Windows.Controls.Button;

namespace AIHub.Controls.OfficeReader;

public sealed partial class ReaderWindow
{
    private Button _speech = null!;
    private ReaderSpeechSession? _speechSession;
    private ReaderSpeechPlayerWindow? _speechPlayer;
    private CancellationTokenSource? _speechCancellation;
    private Task _speechTask = Task.CompletedTask;
    private bool _speaking;
    private string S(string key) => _l("Reader.Speech." + key);

    private void SpeechClicked()
    {
        if (_speaking) { _speechCancellation?.Cancel(); return; }
        _speechTask = SpeakAsync();
    }
    private async Task SpeakAsync()
    {
        var document = _document;
        if (document is null || _loading || _closed || _translating) return;
        ReaderSpeechSession? session = null;
        _speaking = true; _speech.Content = "■"; _speech.ToolTip = S("Stop");
        System.Windows.Automation.AutomationProperties.SetName(_speech, S("Stop"));
        UpdateNavigation();
        try
        {
            var voice = new ReaderAiSettingsStore().Load().Speech.Choice();
            var selected = _selection.HasSelection && _selection.Bounds is { } bounds
                ? new ReaderSpeechRange(bounds.Start.Page, bounds.End.Page - bounds.Start.Page + 1, bounds.Start.Offset, bounds.End.Offset) : null;
            var prompt = new ReaderSpeechPrompt(this, _l, document.Pages.Count, selected, voice);
            if (prompt.ShowDialog() != true || prompt.Range is not { } range || _closed || !ReferenceEquals(document, _document)) return;
            session = _speechSession ??= new();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(session.Token, _load!.Token);
            _speechCancellation = cancellation;
            _status.Text = S("Reading");
            var text = await range.ReadAsync(document, cancellation.Token);
            if (string.IsNullOrWhiteSpace(text)) { _status.Text = S("NoText"); return; }
            using (ApplicationBackgroundOperations.Current?.BeginAuxiliary())
            {
                await ApplicationBackgroundOperations.RetireModelsAsync();
                if (!await PrepareSpeechAsync(voice, cancellation.Token)) { _status.Text = S("Cancelled"); return; }
            }
            cancellation.Token.ThrowIfCancellationRequested();
            var request = new ReaderSpeechRequest(Guid.NewGuid().ToString("N"), text, voice, S("Task"));
            var progress = new Progress<(int Done, int Total)>(p => {
                if (!_closed && _speaking && !cancellation.IsCancellationRequested && ReferenceEquals(session, _speechSession)) _status.Text = string.Format(S("Progress"), p.Done, p.Total);
            });
            string reason = _l("OfficeEditor.AiSpeechReason") + " · " + voice.Id;
            var result = await new ReaderSpeechWork(session).GenerateAsync(request, progress, cancellation.Token,
                journal: CreateReaderAiJournal(reason));
            if (_closed || !ReferenceEquals(document, _document) || cancellation.IsCancellationRequested) return;
            await CloseSpeechPlayerAsync();
            var player = new ReaderSpeechPlayerWindow(this, _l, result, document.SourcePath);
            _speechPlayer = player;
            player.Show(); _status.Text = S("Ready");
            ApplicationBackgroundOperations.Current?.Acknowledge(request.Id);
        }
        catch (OperationCanceledException) { if (!_closed && ReferenceEquals(document, _document)) _status.Text = S("Cancelled"); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            ComponentAuditFailures.Report(error);
            OwnedProcessRegistry.Log("reader_speech_failed", "Office.Reader.Speech", detail: error.GetType().Name);
            if (!_closed && ReferenceEquals(document, _document)) _status.Text = error.Message.StartsWith("Reader.", StringComparison.Ordinal)
                ? _l(error.Message) : error.Message is "Music.Poetry.OtherOperation" or "Another model operation is active." ? _l("ChatSpeech.Busy") : S("Failed");
        }
        finally
        {
            _speechCancellation = null; _speaking = false;
            if (!_closed) { _speech.Content = "🔊"; _speech.ToolTip = S("Title"); System.Windows.Automation.AutomationProperties.SetName(_speech, S("Title")); UpdateNavigation(); }
        }
    }

    private Task<bool> PrepareSpeechAsync(VideoNarratorChoice voice, CancellationToken token)
    {
        var library = new ManagedModelLibraryStore();
        var root = library.Load(VideoNarratorCatalog.ArtifactId(voice.Id))?.ModelsRoot;
        if (string.IsNullOrWhiteSpace(root)) root = new StorageSettingsStore().LoadOrCreate().Models.Locations.Select(p => p.Path).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)) throw new InvalidOperationException("Reader.Speech.StorageRequired");
        using var preparation = new VideoModelPreparation(library);
        preparation.ParallelConnections = new AppSettingsStore().LoadOrCreate().ModelDownloads.MaximumParallelConnections;
        var window = new VideoPreparationWindow(this, _l, VideoNarratorCatalog.Get(voice.Id).Name,
            (download, progress, ct) => preparation.PrepareAsync(root, new("speech", [voice.Id]), download, progress, ct), token);
        return Task.FromResult(window.ShowDialog() == true);
    }

    private async Task CloseSpeechPlayerAsync()
    {
        if (_speechPlayer is not { } player) return;
        _speechPlayer = null; if (player.IsVisible) player.Close(); await player.PendingSave;
    }

    private async Task CloseDocumentSpeechAsync()
    {
        var session = _speechSession; _speechSession = null;
        _speechCancellation?.Cancel(); session?.Cancel();
        try { await CloseSpeechPlayerAsync(); await _speechTask; }
        finally { session?.Dispose(); }
    }
}
