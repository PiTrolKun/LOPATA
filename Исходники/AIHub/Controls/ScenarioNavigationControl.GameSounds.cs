using System.Windows;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class ScenarioNavigationControl
{
    internal GameSounds.GameSoundsControl? GameSoundsView { get; private set; }
    internal bool IsGameSounds { get; private set; }

    private bool LeaveGameSounds()
    {
        if (IsGameSounds && GameSoundsView?.Leave() == false) return false;
        IsGameSounds = false;
        return true;
    }

    internal async Task OpenGameSoundsAsync(Window owner, Func<string> language)
    {
        GameSoundsView ??= new(owner, _localize, language);
        if (await GameSoundsView.EnterAsync()) ShowGameSounds();
    }

    internal async Task RestoreGameSoundsAsync(Window owner, Func<string> language, BackgroundOperationState state, CancellationToken token)
    {
        GameSoundsView ??= new(owner, _localize, language);
        var restore = GameSoundsView.RestoreAsync(state, token);
        ShowGameSounds();
        await restore;
    }

    internal async Task OpenGameSoundProjectAsync(Window owner, Func<string> language, string project)
    {
        GameSoundsView ??= new(owner, _localize, language);
        await GameSoundsView.OpenProjectAsync(project, fromTray: true);
        if (GameSoundsView.IsEntered) ShowGameSounds();
    }

    private void ShowGameSounds()
    {
        LeaveSpeechAssets();
        IsOneC = false;
        IsGameSounds = true;
        SelectedDirectionId = ScenarioNavigationCatalog.Creation;
        Content = GameSoundsView;
        StateChanged?.Invoke();
    }
}
