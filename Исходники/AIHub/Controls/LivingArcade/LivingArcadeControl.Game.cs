using System.Windows;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services.LivingArcade;
using GameHost = AIHub.Controls.LivingArcade.LivingArcadeGameHost;

namespace AIHub.Controls;

public sealed partial class LivingArcadeControl
{
    private void StartRun(string id)
    {
        _shownOperationId = null;
        _runId = id;
        if (!_ready)
        {
            _pendingStartId = id;
            _status.Text = _l("Arcade.WaitWarmup");
            return;
        }
        try
        {
            var run = _store.Load(id) ?? throw new InvalidOperationException("Arcade.MissingRun");
            if (run.CurrentLevel == 1 && !run.FirstEventsPrepared)
            {
                _pendingStartId = id; _status.Text = _l("Arcade.Ai.FirstEvents");
                _ = PrepareFirstEventsAsync(id); return;
            }
            if (run.PendingHero is not null)
            {
                if (!_store.TryAdvanceToReadyLevel(id, out var advanced))
                {
                    _pendingStartId = id; ShowMenuCore(false);
                    _status.Text = _l(run.PendingBossRewardLevel is { } rewardLevel && !run.BossRewards.ContainsKey(rewardLevel)
                        ? "Arcade.WaitReward" : "Arcade.WaitNext");
                    _ = PrepareAheadAsync(); return;
                }
                run = advanced!;
            }
            if (!run.Levels.TryGetValue(run.CurrentLevel, out var level))
            {
                _pendingStartId = id; _status.Text = _l("Arcade.WaitNext");
                _ = PrepareAheadAsync(); return;
            }
            _pendingStartId = null;
            var attempt = _store.GetOrCreateAttempt(id);
            level = level with { MiniEvents = attempt.MiniEvents };
            level = LivingArcadePacing.ForPlay(level);
            level = LivingArcadePickupSchedule.ForPlay(level);
            _body.Content = null; _host?.Dispose();
            _simulation = new LivingArcadeSimulation(level, run.EntryState, run.Attempts, run.DeathProgress, run.Penalty);
            _lastSavedKills = run.EntryState.TotalKills;
            _lastSavedCombatState = CombatStateKey(_simulation.ResultHero);
            _lastExperienceSave = DateTime.UtcNow;
            _experienceSaveErrorReported = false;
            var simulation = _simulation;
            simulation.PenaltyCleared += () => _store.ClearPenaltyIfReached(id, simulation.Snapshot.Progress);
            _host = new GameHost();
            _host.SetBackgroundScene(run.Seed, run.CurrentLevel, level.Scenery);
            _host.Audio.EffectsVolume = (float)_effectsVolume;
            _host.Audio.MusicVolume = (float)_musicVolume;
            _host.Audio.AudioFailed += error => OnGenerationStatus("Arcade.AudioUnavailable");
            _host.FrameAdvanced += OnGameFrame;
            _host.PauseRequested += PauseGame;
            _host.RenderingFailed += error => { PauseGame(); ShowError(error); };
            _host.SetSimulation(_simulation);
            _host.Audio.StartMusic(level.Seed, level.MusicNotes);
            _playing = true; _paused = false; _transitioning = false;
            _actions.Children.Clear();
            BuildGameChrome(_host.BackgroundColor);
            _gameHud.Update(_simulation.Snapshot);
            _status.Text = _l("Arcade.Playing") + " · " + (_language().StartsWith("en") ? level.TitleEn : level.TitleRu);
            _host.Loaded += (_, _) => { if (_playing && !_paused) _host?.Start(); };
            _host.Start();
            _ = PrepareAheadAsync();
        }
        catch (Exception error) { ShowError(error); }
    }

    private void OnGameFrame(LivingArcadeSnapshot snapshot)
    {
        if (_disposed || !_playing || _transitioning) return;
        SaveExperience(false);
        if ((DateTime.UtcNow - _lastHud).TotalMilliseconds >= 100)
        {
            _lastHud = DateTime.UtcNow;
            UpdateGameHud(snapshot);
        }
        if (snapshot.IsDefeated || snapshot.IsComplete)
        {
            _transitioning = true; _host?.Stop();
            var id = _runId;
            var simulation = _simulation;
            if (id is not null && simulation is not null)
                _ = FinishAttemptAsync(id, simulation, snapshot);
        }
    }

    private async Task FinishAttemptAsync(string id, LivingArcadeSimulation simulation, LivingArcadeSnapshot snapshot)
    {
        try
        {
            if (snapshot.IsDefeated) _store.RecordDefeat(id, snapshot.Progress, simulation.ResultHero);
            else _store.CompleteLevel(id, simulation.ResultHero, snapshot);
            // Commit the result first; let the final sound finish before releasing its player.
            await Task.Delay(750, _lifetime);
            if (_disposed || _runId != id || _simulation != simulation) return;
            _playing = false; _body.Content = null; _host?.Dispose(); _host = null;
            ShowMenuChrome();
            _hud.Visibility = Visibility.Collapsed;
            _gameHud.StageHeader.Visibility = Visibility.Visible;
            var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, MaxWidth = 850 };
            panel.Children.Add(LiteraryUi.Text(_l(snapshot.IsDefeated ? "Arcade.Defeat" : "Arcade.LevelComplete"), true));
            panel.Children.Add(LiteraryUi.Text(_l(snapshot.IsDefeated ? "Arcade.DefeatHelp" : "Arcade.CompleteHelp")));
            if (snapshot.BossDefeated && _store.Load(id) is { } finished)
            {
                if (finished.BossRewards.TryGetValue(finished.CurrentLevel, out var reward))
                {
                    panel.Children.Add(LiteraryUi.Text(LivingArcadeHud.BossReward(_l,
                        new() { Reward = reward, RemainingLevels = reward.DurationLevels, StartLevel = finished.CurrentLevel + 1 }, _language()), true));
                    panel.Children.Add(LiteraryUi.Text(LivingArcadeHud.BossRewardDetails(_l, reward)));
                    panel.Children.Add(LiteraryUi.Text(_l("Arcade.BossReward.ModelDescription")));
                    panel.Children.Add(LiteraryUi.Text(_language().StartsWith("en") ? reward.DescriptionEn : reward.DescriptionRu));
                }
                else panel.Children.Add(LiteraryUi.Text(_l("Arcade.WaitReward")));
            }
            _body.Content = panel;
            _actions.Children.Clear();
            _actions.Children.Add(MakeButton(snapshot.IsDefeated ? "Arcade.Retry" : "Arcade.Next", () => StartRun(id), "Arcade.AttemptContinue", true));
            _actions.Children.Add(MakeButton("Arcade.ToMenu", ShowMenu, "Arcade.ToMenu"));
            if (!snapshot.IsDefeated) _ = PrepareAheadAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowError(error); }
    }

    private void PauseGame()
    {
        if (!_playing || _paused) return;
        _paused = true; _host?.Stop();
        SaveExperience();
        _status.Text = _l("Arcade.GamePaused");
        RefreshGamePauseControls();
    }

    private void ResumeGame()
    {
        if (!_playing || !_paused || _transitioning) return;
        _paused = false; _status.Text = _l("Arcade.Playing");
        _host?.Start();
        RefreshGamePauseControls();
    }
}
