using AIHub.Models;

namespace AIHub.Controls;

public sealed partial class LivingArcadeControl
{
    private void UpdateGameHud(LivingArcadeSnapshot snapshot)
    {
        _gameHud.Update(snapshot);
        RefreshGamePauseControls();
    }
}
