using System.Windows.Controls;
using AIHub.Services.VideoPreparation;

namespace AIHub.Controls;

public sealed partial class VideoPreparationControl
{
    private System.Windows.Controls.ContextMenu BoardFrameMenu(VideoBoardFrame frame)
    {
        var menu = new System.Windows.Controls.ContextMenu();
        void Add(string key, System.Action run)
        { var item = new System.Windows.Controls.MenuItem { Header = T(key) };
            System.Windows.Automation.AutomationProperties.SetAutomationId(item, "VideoPrep.Board.Menu." + key + "." + frame.Id);
            item.Click += (_, _) => run(); menu.Items.Add(item); }
        Add("BoardEditPlan", () => EditBoardPlan(frame));
        Add("BoardChooseReference", () =>
        { Session.Project.Storyboard!.SelectedId = frame.Id; Render(); if (_boardImageChoice is { } choice) { choice.Focus(); choice.IsDropDownOpen = true; } });
        Add("BoardClearImage", () => ClearBoardImage(frame));
        Add("BoardRemove", () =>
        { var b = Session.Project.Storyboard!; VideoStoryboard.Capture(Session.Project); b.Frames.Remove(frame); b.SelectedId = ""; Changed(); Checkpoint(); Render(); });
        return menu;
    }
    private void ClearBoardImage(VideoBoardFrame frame)
    { VideoStoryboard.Capture(Session.Project); frame.ImageId = ""; Changed(); Checkpoint(); Render(); }
    private void EditBoardPlan(VideoBoardFrame frame)
    {
        var p = Session.Project;
        var plan = VideoStoryboard.Plans(p).FirstOrDefault(x => x.Shot.Id == frame.ShotId);
        if (plan.Shot is null || !Checkpoint()) return;
        p.Shots!.SelectedSceneId = plan.Scene.Id; p.Shots.SelectedShotId = plan.Shot.Id;
        p.CurrentStep = "shots"; Render(); ScheduleWarmup();
    }
}
