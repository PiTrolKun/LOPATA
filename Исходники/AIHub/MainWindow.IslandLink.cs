using AIHub.Models;
using AIHub.Services;
using Lopata.Island;

namespace AIHub;

public partial class MainWindow
{
    private IslandHostCommands? _islandCommands;

    internal Task<IslandResponse> HandleIslandRequestAsync(IslandRequest request, CancellationToken token)
    {
        if (_backgroundOperations is not { } operations) return Task.FromResult(new IslandResponse("unavailable"));
        _islandCommands ??= new(operations, id => ResumeIslandJobAsync(operations, id), ReceiveIslandDraftAsync,
            IslandJobProgress, () => ScenarioNavigationPage.LetItOutChat?.IsWorking == true);
        return _islandCommands.HandleAsync(request, token);
    }

    private double? IslandJobProgress(BackgroundOperationState state)
    {
        if (state.Kind != ImageBatchBackgroundKind || _batchJob is not { Items.Count: > 0 } batch
            || state.Project != batch.Id) return null;
        return (double)batch.Items.Count(item => item.Status is "ready" or "error") / batch.Items.Count;
    }

    private async Task<string> ReceiveIslandDraftAsync(string text, CancellationToken token)
    {
        if (_openingLetItOut || ScenarioNavigationPage.LetItOutChat?.IsWorking == true) return "busy";
        OpenFromSecondInstance();
        var root = LetItOutModel.ModelsRoot(_storageSettings);
        var path = root is null ? null : LetItOutModel.ModelPath(root);
        if (!System.IO.File.Exists(path))
        {
            await OpenLetItOutAsync();
            token.ThrowIfCancellationRequested();
            if (!System.IO.File.Exists(_letItOutCorePath)) return "preparation";
        }
        else _letItOutCorePath = path;
        token.ThrowIfCancellationRequested();
        if (_backgroundOperations?.BlocksNewWork == true) return "busy";
        var chat = RevealLetItOutChat();
        return chat.ReceiveIslandDraft(text) ? "transferred" : "draft_save_failed";
    }

    private async Task<bool> ResumeIslandJobAsync(BackgroundOperationController operations, string id)
    {
        try
        {
            _backgroundCountdown.Cancel();
            var job = operations.PausedOperations.FirstOrDefault(item => item.Id == id);
            if (job?.Kind is RagBookPreparation.Kind or RagBookAcquisitionWork.Kind)
                RevealRagBooksPage(job.Kind == RagBookAcquisitionWork.Kind);
            await operations.ResumeAsync(id, _backgroundLifetime.Token);
            return true;
        }
        catch (OperationCanceledException) when (_backgroundLifetime.IsCancellationRequested) { return false; }
        catch (Exception error) { ReportBackgroundFailure(error); return false; }
    }
}
