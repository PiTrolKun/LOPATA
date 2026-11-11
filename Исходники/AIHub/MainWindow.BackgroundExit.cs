using AIHub.Services;

namespace AIHub;

public partial class MainWindow
{
    private async Task StopBackgroundForExitAsync()
    {
        if (ScenarioNavigationPage.GameSoundsView?.Checkpoint() == false)
            throw new System.IO.IOException("Game sound project checkpoint failed.");
        if (ScenarioNavigationPage.OneCView?.Checkpoint() == false) throw new System.IO.IOException("1C project checkpoint failed.");
        CheckpointRagBooks();
        CheckpointVectorEditors();
        if (ScenarioNavigationPage.LetItOutChat?.SaveConversationForExit() == false)
            throw new System.IO.IOException("Conversation history was not saved.");
        if (ScenarioNavigationPage.LetItOutChat is { } memoryChat) await memoryChat.CloseMemoryForExitAsync();
        if (ScenarioNavigationPage.SongClip?.Checkpoint() == false) throw new System.IO.IOException("Song-clip checkpoint failed.");
        if (_musicPoetryWindow?.SaveForExit() == false) throw new System.IO.IOException("Songwriting session was not saved.");
        // Confirm the durable record before canceling. A failed write must leave the
        // application open, rather than silently promising a resumable exit.
        if (!LiteraryPage.CheckpointBackgroundState()) throw new System.IO.IOException("Literary checkpoint was not confirmed.");
        _backgroundOperations?.CheckpointForExit();
        SaveActiveSessionCheckpoint(strict: true);
        if (_batchJob is not null) BatchStore.Save(_batchJob);
        else if (_imageAnalysisLiterarySession is not null)
            _imageAnalysisSessionStore.Save(_imageAnalysisLiterarySession, ActiveImageStorage);
        _backgroundCountdown.Cancel();
        _backgroundTimer.Stop();
        StatusText.Text = L("Tray.Exiting");
        await DisposeDictationAsync();
        try { _backgroundLifetime.Cancel(); }
        catch (AggregateException error)
        { OwnedProcessRegistry.Log("background_cancel_callback_failed", "Application", detail: error.GetType().Name); }
        if (_backgroundOperations is not null)
            await _backgroundOperations.WaitForAllIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await ApplicationBackgroundOperations.RetireModelsAsync();
        CheckpointVectorEditors();
    }
}
