namespace AIHub.Controls;

public sealed partial class LivingArcadeControl
{
    private DateTime _lastExperienceSave;
    private long _lastSavedKills;
    private string _lastSavedCombatState = "";
    private bool _experienceSaveErrorReported;

    private void SaveExperience(bool force = true)
    {
        if (!_playing || _runId is not { } id || _simulation is not { } simulation) return;
        var hero = simulation.ResultHero;
        var kills = hero.TotalKills;
        var combatState = CombatStateKey(hero);
        if (kills <= _lastSavedKills && combatState == _lastSavedCombatState
            || !force && (DateTime.UtcNow - _lastExperienceSave).TotalSeconds < 1) return;
        _lastExperienceSave = DateTime.UtcNow;
        try
        {
            if (_store.IsDeleted(id)) return;
            _store.RecordCombatProgress(id, hero);
            _lastSavedKills = kills;
            _lastSavedCombatState = combatState;
            _experienceSaveErrorReported = false;
        }
        catch (Exception error)
        {
            if (_experienceSaveErrorReported) return;
            _experienceSaveErrorReported = true;
            ShowError(new InvalidOperationException("Arcade.ExperienceSaveFailed", error));
        }
    }

    private static string CombatStateKey(AIHub.Models.LivingArcadeHeroState hero) =>
        System.Text.Json.JsonSerializer.Serialize(new { hero.WeaponKind, hero.Companions });
}
