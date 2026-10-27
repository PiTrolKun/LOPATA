using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using AIHub.Services;
using AIHub.Services.VideoPreparation;

namespace AIHub.Controls;

public sealed partial class VideoPreparationControl
{
    private VideoBoardPreviewWindow? _boardPreview;
    private string _boardPreviewProject = "";
    private bool _boardInvalidDuration;
    private TextBlock? _boardSummary;
    private sealed record BoardImageChoice(string Id, string Name) { public override string ToString() => Name; }
    private void BuildStoryboard(StackPanel page)
    {
        if (BuildFinishProgress(page)) return;
        var p = Session.Project; var wasNew = p.Storyboard is null; var b = VideoStoryboard.Ensure(p);
        if (wasNew) { Session.Changed(); _autosave.Start(); }
        _boardInvalidDuration = false;
        page.Children.Add(Text("BoardHint"));
        if (!VideoStoryboard.Current(p)) page.Children.Add(Text("BoardStale"));
        var actions = new WrapPanel();
        actions.Children.Add(Action("BoardPreview", () =>
        {
            if (!VideoPreparationReadiness.BoardReady(p) || _boardInvalidDuration) { _statusKey = "BoardNotReady"; RefreshStatus(); return; }
            _boardPreview?.Close(); _boardPreviewProject = p.Id; _boardPreview = new(Window.GetWindow(this), _localize, p);
            _boardPreview.Closed += (_, _) => _boardPreview = null; _boardPreview.Show();
        }));
        actions.Children.Add(Action("BoardSynchronize", () => { VideoStoryboard.Synchronize(p); Changed(); Checkpoint(); Render(); }));
        foreach (var delta in new[] { -1, 1 })
        { var button = Action(delta == -1 ? "BoardUndo" : "BoardRedo", () => { if (VideoStoryboard.MoveVersion(p, delta)) { Changed(); Checkpoint(); Render(); } }); button.Content = delta == -1 ? "←" : "→"; actions.Children.Add(button); }
        page.Children.Add(actions); BuildBoardAudio(page);
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new StackPanel(); var start = 0d;
        foreach (var frame in b.Frames)
        {
            var pair = VideoStoryboard.Plans(p).FirstOrDefault(x => x.Shot.Id == frame.ShotId); var currentStart = start; start += frame.Seconds;
            var button = Action("BoardChooseFrame", () => { b.SelectedId = frame.Id; Render(); });
            button.Content = $"{b.Frames.IndexOf(frame) + 1}. {pair.Shot?.Name ?? T("BoardMissingPlan")}\n{currentStart:0.###}–{start:0.###} s";
            System.Windows.Automation.AutomationProperties.SetAutomationId(button, "VideoPrep.Board.Frame." + frame.Id); left.Children.Add(button);
        }
        var scroll = new ScrollViewer { Content = left, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalAlignment = VerticalAlignment.Top };
        FitShotsList(scroll, grid, new StackPanel()); grid.Children.Add(scroll);
        var editor = new StackPanel { Margin = new Thickness(12, 0, 0, 0) }; Grid.SetColumn(editor, 1); grid.Children.Add(editor); page.Children.Add(grid);
        var selected = b.Frames.FirstOrDefault(f => f.Id == b.SelectedId) ?? b.Frames.FirstOrDefault();
        if (selected is null) { editor.Children.Add(Text("BoardNoFrames")); return; } b.SelectedId = selected.Id;
        var plan = VideoStoryboard.Plans(p).FirstOrDefault(x => x.Shot.Id == selected.ShotId);
        var title = Text("BoardMissingPlan", true); title.Text = plan.Shot?.Name ?? title.Text; editor.Children.Add(title);
        if (plan.Shot is not null) { var action = Text("ShotsStart"); action.Text = plan.Shot.Start + "\n→ " + plan.Shot.End; editor.Children.Add(action); }
        var duration = new ExampleTextInput(T("ShotsDuration"), T("ShotsDurationExample"), "VideoPrep.Board.Duration");
        duration.Input.Text = selected.Seconds.ToString("R", CultureInfo.InvariantCulture);
        duration.Input.TextChanged += (_, _) =>
        {
            var valid = double.TryParse(duration.Input.Text.Replace(',', '.'), CultureInfo.InvariantCulture, out var seconds) && double.IsFinite(seconds) && seconds is > 0 and <= 86400;
            _boardInvalidDuration = !valid;
            if (valid) { selected.Seconds = seconds; selected.Estimated = false; Changed(); UpdateBoardSummary(); }
            else { _statusKey = "BoardDurationInvalid"; RefreshStatus(); }
        };
        editor.Children.Add(Text("ShotsDuration")); editor.Children.Add(duration);
        var choices = new List<BoardImageChoice> { new("", T("BoardWithoutImage")) };
        choices.AddRange((p.References?.Images ?? []).Select(i => new BoardImageChoice(i.Id, BoardImageName(p, i))));
        if (selected.ImageId != "" && choices.All(c => c.Id != selected.ImageId)) choices.Add(new(selected.ImageId, T("BoardMissingImage")));
        var imageChoice = new System.Windows.Controls.ComboBox { ItemsSource = choices, SelectedItem = choices.First(c => c.Id == selected.ImageId), Margin = new Thickness(0, 4, 0, 10) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(imageChoice, "VideoPrep.Board.Image");
        imageChoice.SelectionChanged += (_, _) => { if (imageChoice.SelectedItem is BoardImageChoice c) { selected.ImageId = c.Id; Changed(); Checkpoint(); Render(); } };
        editor.Children.Add(Text("BoardImage")); editor.Children.Add(imageChoice);
        if (p.References?.Images.FirstOrDefault(i => i.Id == selected.ImageId) is { } image)
            try { var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 700;
                bitmap.UriSource = new Uri(VideoReferences.PathFor(p, image)); bitmap.EndInit(); bitmap.Freeze();
                editor.Children.Add(new System.Windows.Controls.Image { Source = bitmap, MaxHeight = 270, Stretch = System.Windows.Media.Stretch.Uniform }); }
            catch (Exception e) when (e is System.IO.IOException or NotSupportedException) { editor.Children.Add(Text("BoardMissingImage")); }
        var notes = new ExampleTextInput(T("BoardNotes"), T("BoardNotesExample"), "VideoPrep.Board.Notes", multiline: true);
        notes.Input.Text = selected.Notes; notes.Input.MaxLength = 20000; notes.Input.Height = 95; LiterarySpellChecking.Enable(notes.Input, InputLanguage);
        notes.Input.TextChanged += (_, _) => { selected.Notes = notes.Input.Text; Changed(); }; editor.Children.Add(Text("BoardNotes")); editor.Children.Add(notes);
        var move = new WrapPanel();
        foreach (var delta in new[] { -1, 1 }) move.Children.Add(Action(delta == -1 ? "BoardUp" : "BoardDown", () =>
        { var i = b.Frames.IndexOf(selected); var j = i + delta; if (j < 0 || j >= b.Frames.Count) return; VideoStoryboard.Capture(p); (b.Frames[i], b.Frames[j]) = (b.Frames[j], b.Frames[i]); Changed(); Checkpoint(); Render(); }));
        move.Children.Add(Action("BoardRemove", () => { VideoStoryboard.Capture(p); b.Frames.Remove(selected); b.SelectedId = ""; Changed(); Checkpoint(); Render(); })); editor.Children.Add(move);
        _boardSummary = Text("BoardSummary"); page.Children.Add(_boardSummary); UpdateBoardSummary();
    }
    private void UpdateBoardSummary()
    { if (_boardSummary is not null) _boardSummary.Text = string.Format(T("BoardSummary"), Session.Project.Storyboard?.Frames.Count ?? 0, VideoStoryboard.Duration(Session.Project).ToString("0.###", CultureInfo.InvariantCulture)); }
    private static string BoardImageName(VideoPreparationProject p, VideoReferenceImage image)
    {
        var variant = p.References?.Drawing?.Variants.FirstOrDefault(v => v.ImageId == image.Id);
        var prompt = p.References?.Plan?.Items.FirstOrDefault(i => i.Id == variant?.PromptId);
        return prompt is null ? image.Name : prompt.Name + " · " + ((p.References!.Drawing!.Variants.Where(v => v.PromptId == prompt.Id).ToList().IndexOf(variant!) + 1).ToString(CultureInfo.InvariantCulture));
    }
}
