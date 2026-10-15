namespace AIHub.Controls;

public sealed partial class MusicPreparationControl
{
    private bool _removingModel;
    public Func<string, bool>? CanRemoveModel { get; set; }
    public event Func<string, Task>? RemoveModelRequested;
    private static bool PendingMusicOperation => AIHub.Services.ApplicationBackgroundOperations.Current?.FindOperation(AIHub.Services.MusicGenerationRunner.BackgroundKind) is
        { Phase: not (AIHub.Models.BackgroundOperationPhase.Completed or AIHub.Models.BackgroundOperationPhase.Failed or AIHub.Models.BackgroundOperationPhase.Canceled) };
    public bool CanRemoveModels => !_disposed && !IsBusy && !PendingMusicOperation && _workspace?.Session.HasPendingOrRunning != true;

    public void SetModelRemovalBusy(bool busy)
    {
        _removingModel = busy; IsBusy = busy;
        if (!busy) {
            _ready = false; _cards = []; _preparedHardware = null;
            _workspace?.Editor.ResetTokenizer();
        }
        Render();
    }
}
