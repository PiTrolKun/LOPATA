using System.Windows;
using System.Windows.Controls;
using AIHub.Services.VideoPreparation;

namespace AIHub.Controls;

public sealed class VideoShotOptionsWindow : Window
{
    private readonly Func<string, string> _localize;
    private readonly Action<string> _choose;
    private readonly StackPanel[] _columns = [new(), new(), new()];
    public VideoShotOptionsWindow(Window? owner, Func<string, string> localize, Action<string> choose)
    {
        _localize = localize; _choose = choose; VideoReferenceWindowUi.Configure(this, owner, localize("VideoPrep.ShotsAlternatives"));
        var grid = new Grid { Margin = new Thickness(12) };
        for (var i = 0; i < 3; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var border = new Border { Padding = new Thickness(10), Margin = new Thickness(4), BorderThickness = new Thickness(1) };
            border.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            border.Child = new ScrollViewer { Content = _columns[i], VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetColumn(border, i); grid.Children.Add(border);
        }
        Content = grid;
    }
    public void Update(IEnumerable<VideoShotCandidate> candidates)
    {
        foreach (var column in _columns) column.Children.Clear();
        for (var level = 1; level <= 3; level++)
        {
            var column = _columns[level - 1]; var candidate = candidates.LastOrDefault(c => c.Freedom == level);
            column.Children.Add(VideoReferenceWindowUi.Text(_localize("VideoPrep.ShotsFreedom." + level)));
            if (candidate is null || !candidate.Complete) { column.Children.Add(VideoReferenceWindowUi.Text(_localize("VideoPrep.ShotsOptionPending"))); continue; }
            foreach (var scene in candidate.Scenes)
            {
                column.Children.Add(VideoReferenceWindowUi.Text(scene.Name + "\n" + scene.Intent));
                foreach (var shot in scene.Shots) column.Children.Add(VideoReferenceWindowUi.Text("• " + shot.Name + "\n" + shot.Start + " → " + shot.End));
            }
            column.Children.Add(VideoReferenceWindowUi.Button(_localize("VideoPrep.ShotsChooseOption"), "Shots.Choose." + candidate.Id, () => _choose(candidate.Id)));
        }
    }
}
