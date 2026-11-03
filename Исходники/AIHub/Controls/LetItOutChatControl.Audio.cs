using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class LetItOutChatControl
{
    private Func<CancellationToken, Task<bool>>? _prepareAudio;
    private Action? _audioNotice;

    internal void ConfigureAudioAttachments(Func<CancellationToken, Task<bool>> prepare, Action notice)
    { _prepareAudio = prepare; _audioNotice = notice; }

    internal void ShowAttachmentStage(string key)
    { if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(() => { if (!_disposed && IsWorking) { _statusKey = key; Availability(); } }); }

    private async Task<bool> PrepareAudioAttachmentsAsync()
    {
        if (!_draftAttachments.Concat(_entries.SelectMany(e => e.Attachments ?? [])).Any(id =>
            _library?.Attachment(_sessionId, id).Kind == "audio" && _library.AudioTranscript(_sessionId, id)?.Complete != true)) return true;
        _importingAttachments = true; Availability();
        try { return _prepareAudio is not null && await _prepareAudio(ApplicationBackgroundOperations.ExitToken); }
        catch (OperationCanceledException) { return false; }
        catch (Exception error) { AttachmentError(error); return false; }
        finally { _importingAttachments = false; Availability(); }
    }
}
