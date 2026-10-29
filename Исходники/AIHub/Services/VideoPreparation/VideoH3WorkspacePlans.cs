namespace AIHub.Services.VideoPreparation;

internal sealed record VideoH3PlanItem(VideoScenePlan Scene, VideoShotPlan Shot, VideoBoardFrame? Frame, string Prompt, bool HasWorkingText)
{
    internal double Seconds => Frame?.Seconds ?? (Shot.Duration > 0 ? Shot.Duration : 5);
}
internal static class VideoH3WorkspacePlans
{
    internal static IReadOnlyList<VideoH3PlanItem> From(VideoPreparationProject p)
    {
        var plans = VideoStoryboard.Plans(p).ToArray();
        var ordered = p.Storyboard is { Frames.Count: > 0 } b
            ? b.Frames.Select(f => (Pair: plans.FirstOrDefault(x => x.Shot.Id == f.ShotId), Frame: (VideoBoardFrame?)f)).Where(x => x.Pair.Shot is not null)
            : plans.Select(x => (Pair: x, Frame: (VideoBoardFrame?)null));
        return ordered.Select(x =>
        {
            var translated = p.Shots is { } d && VideoShots.TranslationCurrent(d, x.Pair.Scene)
                ? VideoShots.SelectedTranslation(d, x.Pair.Scene.Id)?.Scene.Shots.FirstOrDefault(t => t.Id == x.Pair.Shot.Id) : null;
            return new VideoH3PlanItem(x.Pair.Scene, x.Pair.Shot, x.Frame, VideoPreparationExport.Prompt(translated ?? x.Pair.Shot), translated is not null);
        }).ToArray();
    }
}
