using AIHub.Services.LivingArcade;
using AIHub.Models;

namespace AIHub.Controls.LivingArcade;

public sealed partial class LivingArcadeGameHost
{
    private readonly LivingArcadeBackground _background = new();
    private readonly LivingArcadePerspectiveBackground _perspectiveBackground = new();
    private readonly LivingArcadeGeneratedBackground _generatedBackground = new();
    private LivingArcadeGeneratedScenery? _generatedScenery;
    private LivingArcadeScenery _backgroundScene = LivingArcadeSceneryPolicy.Create(0, 1);
    private bool _explicitBackgroundScene;

    public LivingArcadeScenery BackgroundScene => _backgroundScene;
    public bool HasGeneratedScenery => _generatedBackground.IsPrepared;
    public LivingArcadeViewMode ViewMode { get; private set; } = LivingArcadeViewMode.TopDown;
    private bool IsSceneryPrepared => _generatedBackground.IsPrepared || (ViewMode == LivingArcadeViewMode.Perspective
        ? _perspectiveBackground.IsPrepared : _background.IsPrepared);

    public void SetBackgroundScene(int runSeed, int levelNumber, LivingArcadeGeneratedScenery? scenery = null)
    {
        _backgroundScene = LivingArcadeSceneryPolicy.Create(runSeed, levelNumber);
        _generatedScenery = scenery;
        ViewMode = LivingArcadePresentationPolicy.ModeFor(runSeed, levelNumber);
        _explicitBackgroundScene = true;
        PrepareScenery();
        InvalidateVisual();
    }

    private void PrepareScenery()
    {
        if (!_explicitBackgroundScene)
            _backgroundScene = LivingArcadeSceneryPolicy.Create(0, _simulation?.Snapshot.GameLevel ?? 1);
        if (_generatedScenery is not null)
        {
            try
            {
                _generatedBackground.Prepare(_generatedScenery);
                _background.Clear(); _perspectiveBackground.Clear();
                BackgroundColor = _generatedBackground.BackgroundColor;
                return;
            }
            catch (Exception error) when (error is System.IO.InvalidDataException or FormatException or ArgumentException)
            {
                // Optional decoration must not prevent a valid battle from opening.
                _generatedBackground.Clear();
            }
        }
        else _generatedBackground.Clear();
        if (ViewMode == LivingArcadeViewMode.Perspective)
        {
            _background.Clear();
            _perspectiveBackground.Prepare(_backgroundScene.Kind, _backgroundScene.Seed, _backgroundScene.Variant);
            BackgroundColor = _perspectiveBackground.BackgroundColor;
        }
        else
        {
            _perspectiveBackground.Clear();
            _background.Prepare(_backgroundScene.Kind, _backgroundScene.Seed, _backgroundScene.Variant);
            BackgroundColor = _background.BackgroundColor;
        }
    }

    private void ClearScenery()
    { _background.Clear(); _perspectiveBackground.Clear(); _generatedBackground.Clear(); _sceneActors.Clear(); }
}
