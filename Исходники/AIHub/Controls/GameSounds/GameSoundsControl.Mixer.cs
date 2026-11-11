using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AIHub.Services;
using AIHub.Services.GameSounds;

namespace AIHub.Controls.GameSounds;

internal sealed partial class GameSoundsControl
{
    private void OpenMixer()
    {
        if (_mixer is not null) { _mixer.Activate(); return; }
        if (Project.Tracks.Count == 0) return;
        var window = Dialog("Mixer", 1160, 680);
        _mixer = window;
        var player = new GameSoundPlayer();
        StopPlayback();
        var root = new DockPanel { Margin = new(18) };
        var footer = Row();
        var play = Action("PlayMix", async () =>
        {
            if (player.Playing) player.Pause();
            else await player.PlayAsync(Project.Sources(true), Project.Loop, ApplicationBackgroundOperations.ExitToken);
        }, primary: true);
        player.PlaybackChanged += () => Dispatcher.InvokeAsync(() =>
            play.Content = LabelText(T(player.Playing ? "Pause" : "PlayMix")));
        footer.Children.Add(play);
        footer.Children.Add(Check("Loop", Project.Loop, value =>
        {
            Project.Loop = value; player.SetLoop(value); Changed();
        }));
        footer.Children.Add(Action("SaveMixRecipe", () => ExportAsync(true, false, window)));
        var format = AudioFormatPicker(); format.Margin = new(0, 2, 4, 2); footer.Children.Add(format);
        footer.Children.Add(Action("SaveMixFile", () => ExportAsync(true, true, window)));
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var rows = new StackPanel();
        var axis = TrackLayout();
        Add(axis, Label("Layer", true));
        var ticks = new Canvas { Height = 22, Margin = new(1, 0, 8, 0) };
        var seconds = GameSoundRecipes.MaxMilliseconds / 1000;
        for (var second = 0; second <= seconds; second++)
            ticks.Children.Add(LabelText(F("TimelineSecond", second)));
        ticks.SizeChanged += (_, _) =>
        {
            for (var second = 0; second <= seconds; second++)
            {
                var text = (TextBlock)ticks.Children[second];
                text.Measure(new(double.PositiveInfinity, double.PositiveInfinity));
                var width = text.DesiredSize.Width;
                Canvas.SetLeft(text, Math.Clamp(second * ticks.ActualWidth / seconds - width / 2,
                    0, Math.Max(0, ticks.ActualWidth - width)));
            }
        };
        Add(axis, ticks, 1); Add(axis, Label("TrackVolume", true), 2); rows.Children.Add(axis);
        foreach (var source in Project.Sources(true))
        {
            var row = TrackLayout();
            var id = source.Recipe.Id;
            GameSoundTrack Track() => Project.Tracks.Single(t => t.RecipeId == id);
            void Update(GameSoundTrack value)
            {
                var index = Project.Tracks.FindIndex(t => t.RecipeId == id);
                if (index < 0) return;
                Project.Tracks[index] = value;
                player.Invalidate(); Changed();
            }
            var check = MusicWishUi.Check(source.Recipe.Name, source.Enabled, value => Update(Track() with { Enabled = value }));
            check.Style = null;
            AutomationProperties.SetName(check, F("TrackFor", source.Recipe.Name));
            Add(row, check);
            var timeline = new Canvas { Height = 35, Margin = new(1, 4, 8, 4), ClipToBounds = true };
            timeline.SetResourceReference(BackgroundProperty, "InputBrush");
            var clip = new Thumb { Height = 28, ToolTip = T("DragLayer"), Cursor = System.Windows.Input.Cursors.SizeWE, Focusable = true };
            var template = new ControlTemplate(typeof(Thumb));
            var fill = new FrameworkElementFactory(typeof(Border));
            fill.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            fill.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            template.VisualTree = fill;
            clip.Template = template;
            AutomationProperties.SetName(clip, source.Recipe.Name);
            timeline.Children.Add(clip); Canvas.SetTop(clip, 3);
            void Draw()
            {
                var track = Track();
                var ratio = timeline.ActualWidth / GameSoundRecipes.MaxMilliseconds;
                clip.Width = Math.Max(1, ratio * source.Recipe.Sound.DurationMs);
                clip.Opacity = track.Enabled ? 1 : .4;
                Canvas.SetLeft(clip, track.StartMs * ratio);
                clip.ToolTip = F("LayerPosition", track.StartMs, source.Recipe.Sound.DurationMs);
                AutomationProperties.SetHelpText(clip, (string)clip.ToolTip);
            }
            var position = (double)source.StartMs;
            clip.DragStarted += (_, _) => position = Track().StartMs;
            clip.DragDelta += (_, e) =>
            {
                if (timeline.ActualWidth < 1) return;
                position = Math.Clamp(position + e.HorizontalChange / timeline.ActualWidth * GameSoundRecipes.MaxMilliseconds,
                    0, GameSoundRecipes.MaxMilliseconds - source.Recipe.Sound.DurationMs);
                Update(Track() with { StartMs = (int)Math.Round(position) }); Draw();
            };
            clip.KeyDown += (_, e) =>
            {
                var delta = e.Key switch
                {
                    System.Windows.Input.Key.Left => -10,
                    System.Windows.Input.Key.Right => 10,
                    _ => 0
                };
                if (delta == 0) return;
                Update(Track() with { StartMs = Math.Clamp(Track().StartMs + delta, 0,
                    GameSoundRecipes.MaxMilliseconds - source.Recipe.Sound.DurationMs) });
                Draw(); e.Handled = true;
            };
            check.Checked += (_, _) => Draw(); check.Unchecked += (_, _) => Draw();
            timeline.SizeChanged += (_, _) => Draw();
            Add(row, timeline, 1);
            var level = Number("TrackVolume", "Percent", 0, 100, () => Track().Level,
                value => Update(Track() with { Level = value }), showLabel: false);
            Add(row, level, 2); rows.Children.Add(row);
        }
        root.Children.Add(Scroll(rows)); window.Content = root;
        window.Closed += (_, _) => { player.Dispose(); _mixer = null; Checkpoint(); };
        window.ShowDialog();
    }

    private static Grid TrackLayout()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new() { Width = new(165) });
        grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star), MinWidth = 110 });
        grid.ColumnDefinitions.Add(new() { Width = new(150) });
        return grid;
    }
}
