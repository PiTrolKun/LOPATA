using System.IO;
using System.Windows;
using AIHub.Controls.OfficeReader;
using AIHub.Controls.VideoEditor;
using AIHub.Services;
using AIHub.Services.OfficeReader;
using AIHub.Services.VideoEditor;

namespace AIHub.Controls.OfficeEditor;

public sealed partial class EditorWindow
{
    private ReaderSpeechSession? _speechSession;
    private ReaderSpeechPlayerWindow? _speechPlayer;
    private CancellationTokenSource? _speechCancellation;
    private Task _speechTask = Task.CompletedTask;
    private bool _speaking;
    private Task SpeakAsync()
    {
        if (_speaking) { _speechCancellation?.Cancel(); return _speechTask; }
        return _speechTask = SpeakCoreAsync();
    }
    private async Task SpeakCoreAsync()
    {
        if (!_ready || _closed) return;
        string S(string key) => _l("Reader.Speech." + key);
        _speaking = true; _speechButton.Content = "■";
        try
        {
            var voice = new ReaderAiSettingsStore().Load().Speech.Choice();
            var selected = await RunAsync(new { action = "text", selection = true });
            var selectedText = selected?.GetString();
            var selection = string.IsNullOrWhiteSpace(selectedText) ? null : new ReaderSpeechRange(0, 1);
            var prompt = new ReaderSpeechPrompt(this, _l, _pages, selection, voice);
            if (prompt.ShowDialog() != true || prompt.Range is not { } range || _closed) return;
            var text = ReferenceEquals(range, selection) ? selectedText : (await RunAsync(new { action = "pageText", first = range.FirstPage, count = range.Count }))?.GetString();
            if (string.IsNullOrWhiteSpace(text)) { _status.Text = S("NoText"); return; }
            var session = _speechSession ??= new();
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(session.Token, _lifetime.Token); _speechCancellation = cancel;
            using (ApplicationBackgroundOperations.Current?.BeginAuxiliary())
            {
                await ApplicationBackgroundOperations.RetireModelsAsync();
                if (!PrepareSpeech(voice, cancel.Token)) return;
            }
            var request = new ReaderSpeechRequest(Guid.NewGuid().ToString("N"), text, voice, S("Task"));
            var progress = new Progress<(int Done, int Total)>(p => { if (!_closed) _status.Text = string.Format(S("Progress"), p.Done, p.Total); });
            var result = await new ReaderSpeechWork(session).GenerateAsync(request, progress, cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
            if (_speechPlayer is { } old) { old.Close(); await old.PendingSave; }
            _speechPlayer = new ReaderSpeechPlayerWindow(this, _l, result, _savePath ?? T("NewDocument")); _speechPlayer.Show();
            ApplicationBackgroundOperations.Current?.Acknowledge(request.Id); _status.Text = S("Ready");
        }
        catch (OperationCanceledException) { if (!_closed) _status.Text = S("Cancelled"); }
        catch (Exception error) { Failure(error); }
        finally { _speaking = false; _speechCancellation = null; _speechButton.Content = "🔊"; }
    }
    private bool PrepareSpeech(VideoNarratorChoice voice, CancellationToken token)
    {
        var library = new ManagedModelLibraryStore();
        var root = library.Load(VideoNarratorCatalog.ArtifactId(voice.Id))?.ModelsRoot;
        if (string.IsNullOrWhiteSpace(root)) root = new StorageSettingsStore().LoadOrCreate().Models.Locations.Select(p => p.Path).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
        if (string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException("Reader.Speech.StorageRequired");
        using var preparation = new VideoModelPreparation(library);
        preparation.ParallelConnections = new AppSettingsStore().LoadOrCreate().ModelDownloads.MaximumParallelConnections;
        return new VideoPreparationWindow(this, _l, VideoNarratorCatalog.Get(voice.Id).Name,
            (download, progress, ct) => preparation.PrepareAsync(root, new("speech", [voice.Id]), download, progress, ct), token).ShowDialog() == true;
    }
    private async Task CloseSpeechAsync()
    {
        _speechCancellation?.Cancel(); _speechSession?.Cancel();
        if (_speechPlayer is { } player) { player.Close(); await player.PendingSave; _speechPlayer = null; }
        await _speechTask; _speechSession?.Dispose(); _speechSession = null;
    }
}
