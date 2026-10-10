using AIHub.Services;

namespace AIHub;

public partial class MainWindow
{
    private async Task StopBackgroundForExitAsync()
    {
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
        try { _backgroundLifetime.Cancel(); }
        catch (AggregateException error)
        { OwnedProcessRegistry.Log("background_cancel_callback_failed", "Application", detail: error.GetType().Name); }
        if (_backgroundOperations is not null)
            await _backgroundOperations.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await ApplicationBackgroundOperations.RetireModelsAsync();
    }
}
