using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class ScenarioNavigationControl
{
    internal string? RecoveryScenarioId =>
        IsNeuroConstructor ? ScenarioNavigationCatalog.NeuroConstructor :
        IsCharacterKnowledge ? ScenarioNavigationCatalog.CharacterKnowledge :
        IsImageLayers ? ScenarioNavigationCatalog.ImageLayers :
        IsGameSounds ? ScenarioNavigationCatalog.GameSounds :
        IsOneC ? ScenarioNavigationCatalog.OneC :
        IsVisualConstructor ? ScenarioNavigationCatalog.VisualConstructor :
        IsSpeechAssets ? ScenarioNavigationCatalog.SpeechAssets :
        IsLivingArcade ? ScenarioNavigationCatalog.LivingArcade :
        IsLetItOutChat ? ScenarioNavigationCatalog.LetItOut :
        IsSongClip ? ScenarioNavigationCatalog.SongClip :
        IsShorts ? ScenarioNavigationCatalog.Shorts :
        IsVisualScenario ? ScenarioNavigationCatalog.VisualScenario :
        IsAnimatedAssets ? ScenarioNavigationCatalog.AnimatedAssets :
        IsVideoPreparation ? ScenarioNavigationCatalog.VideoGeneration :
        IsSandboxLanding ? ScenarioNavigationCatalog.Sandbox : PlaceholderScenarioId;

    internal double? RecoveryScrollOffset => _scenarioScroll?.VerticalOffset;

    internal void RestoreRecoveryScroll(double? offset)
    {
        if (offset is { } value && double.IsFinite(value) && value >= 0)
            _scenarioScroll?.ScrollToVerticalOffset(value);
    }
}
