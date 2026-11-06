using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using AIHub.Services;
using AIHub.Services.LivingArcade;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using Panel = System.Windows.Controls.Panel;
using Path = System.Windows.Shapes.Path;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace AIHub.Controls;

/// <summary>Places the game's status surfaces outside its normalized combat field.</summary>
public sealed partial class LivingArcadeControl
{
    private Button? _gamePauseButton, _gameResumeButton, _preparationButton;
    private BackgroundOperationController? _preparationController;

    private void BuildGameChrome(string backgroundColor)
    {
        ResetPauseShortcut(null, EventArgs.Empty);
        _hud.Visibility = Visibility.Collapsed;
        _actions.Visibility = Visibility.Collapsed;
        _gameHud.StageHeader.Visibility = Visibility.Visible;
        _gameFooter.Visibility = Visibility.Visible;
        Detach(_gameHud.HeroPanel);
        Detach(_gameHud.Abilities);
        var surface = new Grid { ClipToBounds = true,
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(backgroundColor)) };
        AutomationProperties.SetAutomationId(surface, "Arcade.Game.Surface");
        var left = new ColumnDefinition { Width = new GridLength(260) };
        var right = new ColumnDefinition { Width = new GridLength(260) };
        surface.ColumnDefinitions.Add(left);
        surface.ColumnDefinitions.Add(new());
        surface.ColumnDefinitions.Add(right);
        var hero = new Viewbox { Child = _gameHud.HeroPanel, Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(5, 8, 10, 4) };
        AutomationProperties.SetAutomationId(hero, "Arcade.Game.HeroLane");
        surface.Children.Add(hero);
        if (_host is not null)
        {
            Grid.SetColumn(_host, 1);
            AutomationProperties.SetAutomationId(_host, "Arcade.Game.FieldLane");
            surface.Children.Add(_host);
        }
        var rightLane = new Border { IsHitTestVisible = false };
        Grid.SetColumn(rightLane, 2);
        AutomationProperties.SetAutomationId(rightLane, "Arcade.Game.RightLane");
        surface.Children.Add(rightLane);
        surface.SizeChanged += (_, _) =>
        {
            // Keep the road centered; narrow desktop windows reduce both side margins equally.
            var side = Math.Clamp((surface.ActualWidth - 560) / 2, 176, 260);
            left.Width = right.Width = new GridLength(side);
        };
        _body.Content = surface;
        BuildGameFooter();
    }

    private void BuildGameFooter()
    {
        Detach(_gameHud.Abilities);
        _gameFooter.Children.Clear(); _gameFooter.ColumnDefinitions.Clear();
        _gameFooter.Margin = new Thickness(0, 8, 0, 0);
        _gameFooter.MinHeight = 94;
        AutomationProperties.SetAutomationId(_gameFooter, "Arcade.Game.Footer");
        _gameFooter.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _gameFooter.ColumnDefinitions.Add(new());
        _gameFooter.ColumnDefinitions.Add(new() { Width = new GridLength(210) });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        AutomationProperties.SetAutomationId(actions, "Arcade.Game.Actions");
        _gamePauseButton = GameIconButton("pause", "Arcade.PauseGame", PauseGame);
        _gameResumeButton = GameIconButton("play", "Arcade.ResumeGame", ResumeGame);
        // Mouse clicks keep the field's keyboard target, including while its rendering is paused.
        _gamePauseButton.Focusable = _gameResumeButton.Focusable = false;
        actions.Children.Add(_gamePauseButton); actions.Children.Add(_gameResumeButton);
        var menu = MakeButton("Arcade.ToMenu", ShowMenu, "Arcade.ToMenu");
        menu.FontSize = 13; menu.Height = 40; menu.MinHeight = 40;
        menu.Padding = new Thickness(12, 5, 12, 5); menu.Margin = new Thickness(4, 0, 0, 0);
        actions.Children.Add(menu);
        _gameFooter.Children.Add(actions);
        var abilities = new Viewbox { Child = _gameHud.Abilities, Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(12, 0, 12, 0),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom };
        Grid.SetColumn(abilities, 1); _gameFooter.Children.Add(abilities);
        var sound = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
        AutomationProperties.SetAutomationId(sound, "Arcade.Game.Sound");
        _preparationButton = GameIconButton("prepare-pause", "Arcade.PauseGeneration", () => _ = TogglePreparationAsync());
        _preparationButton.HorizontalAlignment = HorizontalAlignment.Right;
        _preparationButton.Margin = new Thickness(0, 0, 0, 3);
        sound.Children.Add(_preparationButton);
        sound.Children.Add(GameVolume("effects", "Arcade.SoundEffects", "Arcade.EffectsVolume", _effectsVolume,
            value => { _effectsVolume = value; if (_host is not null) _host.Audio.EffectsVolume = (float)value; }));
        sound.Children.Add(GameVolume("music", "Arcade.Music", "Arcade.MusicVolume", _musicVolume,
            value => { _musicVolume = value; if (_host is not null) _host.Audio.MusicVolume = (float)value; }));
        Grid.SetColumn(sound, 2); _gameFooter.Children.Add(sound);
        RefreshGamePauseControls(); RefreshPreparationControl();
    }

    private void ShowMenuChrome()
    {
        ResetPauseShortcut(null, EventArgs.Empty);
        _gameFooter.Visibility = Visibility.Collapsed;
        _gameHud.StageHeader.Visibility = Visibility.Collapsed;
        _hud.Visibility = Visibility.Visible;
        _actions.Visibility = Visibility.Visible;
    }

    private Button GameIconButton(string glyph, string key, Action action)
    {
        var button = MakeButton(key, action, key);
        button.Content = GameIcon(glyph, button); button.Width = 40; button.Height = 40;
        button.MinWidth = 40; button.MinHeight = 40;
        button.Padding = new Thickness(9); button.Margin = new Thickness(0, 0, 5, 0);
        button.ToolTip = _l(key); AutomationProperties.SetName(button, _l(key));
        return button;
    }

    private FrameworkElement GameVolume(string glyph, string key, string id, double value, Action<double> changed)
    {
        var row = new Grid { Height = 25 };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(30) }); row.ColumnDefinitions.Add(new());
        var icon = GameIcon(glyph); icon.ToolTip = _l(key);
        icon.HorizontalAlignment = HorizontalAlignment.Left; row.Children.Add(icon);
        var slider = new Slider { Minimum = 0, Maximum = 1, Value = value,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(1, 0, 5, 0), ToolTip = _l(key) };
        AutomationProperties.SetAutomationId(slider, id); AutomationProperties.SetName(slider, _l(key));
        slider.ValueChanged += (_, _) => changed(slider.Value);
        Grid.SetColumn(slider, 1); row.Children.Add(slider);
        return row;
    }

    private static FrameworkElement GameIcon(string glyph, Button? owner = null)
    {
        var data = glyph switch
        {
            "pause" => "M4,3 H9 V21 H4 Z M15,3 H20 V21 H15 Z",
            "play" => "M6,3 L21,12 L6,21 Z",
            "music" => "M9,4 L21,2 V16 C21,22 13,22 13,17 C13,14 16,13 18,14 V7 L12,8 V18 C12,24 4,24 4,19 C4,16 7,15 9,16 Z",
            "prepare-play" => "M2,2 H22 V22 H2 Z M5,5 V19 H19 V5 Z M9,7 L17,12 L9,17 Z",
            "prepare-pause" => "M2,2 H22 V22 H2 Z M5,5 V19 H19 V5 Z M8,7 H11 V17 H8 Z M13,7 H16 V17 H13 Z",
            _ => "M2,9 H7 L13,4 V20 L7,15 H2 Z M16,7 L18,5 C23,9 23,15 18,19 L16,17 C20,14 20,10 16,7 Z"
        };
        var path = new Path { Data = Geometry.Parse(data), Stretch = Stretch.Uniform, Width = 20, Height = 20 };
        if (owner is null) path.SetResourceReference(Shape.FillProperty, "TextPrimaryBrush");
        else path.SetBinding(Shape.FillProperty, new System.Windows.Data.Binding(nameof(Button.Foreground)) { Source = owner });
        return path;
    }

    private void RefreshGamePauseControls()
    {
        if (_gamePauseButton is not null) _gamePauseButton.IsEnabled = _playing && !_paused && !_transitioning;
        if (_gameResumeButton is not null) _gameResumeButton.IsEnabled = _playing && _paused && !_transitioning;
    }

    private void RefreshPreparationControl()
    {
        if (_preparationButton is null) return;
        var controller = ApplicationBackgroundOperations.Current;
        if (!ReferenceEquals(_preparationController, controller))
        {
            DetachPreparationObserver();
            _preparationController = controller;
            if (_preparationController is not null) _preparationController.Changed += OnPreparationChanged;
        }
        var pending = controller is null ? null : LivingArcadePendingWork.Find(controller, _runId, _shownOperationId);
        var resume = pending is not null && pending.State.Phase is not
            (Models.BackgroundOperationPhase.Running or Models.BackgroundOperationPhase.Pausing);
        var key = resume ? "Arcade.ResumeGeneration" : "Arcade.PauseGeneration";
        _preparationButton.IsEnabled = pending is not null;
        _preparationButton.Content = GameIcon(resume ? "prepare-play" : "prepare-pause", _preparationButton);
        _preparationButton.ToolTip = _l(key);
        AutomationProperties.SetAutomationId(_preparationButton, key);
        AutomationProperties.SetName(_preparationButton, _l(key));
    }

    private void OnPreparationChanged() => Dispatcher.InvokeAsync(() =>
    {
        if (!_disposed) RefreshPreparationControl();
    });

    private void DetachPreparationObserver()
    {
        if (_preparationController is not null) _preparationController.Changed -= OnPreparationChanged;
        _preparationController = null;
    }

    private async Task TogglePreparationAsync()
    {
        var controller = ApplicationBackgroundOperations.Current;
        var pending = controller is null ? null : LivingArcadePendingWork.Find(controller, _runId, _shownOperationId);
        if (pending is null) return;
        if (pending.State.Phase is Models.BackgroundOperationPhase.Running or Models.BackgroundOperationPhase.Pausing)
            await PauseGenerationAsync();
        else await ResumeGenerationAsync();
        RefreshPreparationControl();
    }

    private static void Detach(FrameworkElement element)
    {
        if (element.Parent is Panel panel) panel.Children.Remove(element);
        else if (element.Parent is Decorator decorator) decorator.Child = null;
        else if (element.Parent is ContentControl content) content.Content = null;
    }
}
