using System.IO;

namespace AIHub.Services.VideoPreparation;

public sealed record VideoPreparationIssue(string Key, string Step, bool Blocking, string Detail = "");
public static class VideoPreparationReadiness
{
    public static IReadOnlyList<VideoPreparationIssue> Check(VideoPreparationProject p)
    {
        var issues = new List<VideoPreparationIssue>();
        void Add(bool condition, string key, string step, bool blocking = true, string detail = "") { if (condition) issues.Add(new(key, step, blocking, detail)); }
        Add(p.Form == "", "CheckForm", "form"); Add(string.IsNullOrWhiteSpace(p.IdeaText), "CheckIdea", "idea");
        Add(!VideoTranslationHistory.IsCurrent(p), "CheckTranslation", "translation");
        Add(p.Development is null || string.IsNullOrWhiteSpace(p.Development.IdeaText), "CheckDevelopment", "development");
        Add(p.Development is not null && (!VideoDevelopment.SourceCurrent(p) || !VideoTranslationHistory.IsCurrent(p.Development)), "CheckDevelopmentTranslation", "development_translation");
        Add(p.World is null || p.World.Cards.Count == 0 || p.World.Cards.Any(c => string.IsNullOrWhiteSpace(c.Name) || string.IsNullOrWhiteSpace(c.Text.IdeaText)), "CheckWorld", "world");
        Add(p.World is not null && (!VideoWorld.SourceCurrent(p) || !VideoTranslationHistory.IsCurrent(p.World.Working)), "CheckWorldTranslation", "world_translation");
        Add(!VideoShots.Ready(p) || !VideoShots.Current(p), "CheckShots", "shots");
        Add(p.Shots is null || p.Shots.Scenes.Any(s => !VideoShots.TranslationCurrent(p.Shots, s) || !VideoShots.SameStructure(s, VideoShots.SelectedTranslation(p.Shots, s.Id)?.Scene)), "CheckShotsTranslation", "shots_translation");
        Add(p.References is null || p.References.Choice == "", "CheckReferences", "references");
        foreach (var binding in p.References?.Bindings ?? []) Add(VideoReferences.NeedsReview(p, binding), "CheckReferenceReview", "references", detail: binding.CardName);
        foreach (var a in p.Attachments) Add(!File.Exists(a.Path), "CheckMissingFile", a.Role.StartsWith("video-board/", StringComparison.Ordinal) ? "storyboard" : "references", detail: Path.GetFileName(a.Path));
        var board = p.Storyboard; var plans = VideoStoryboard.Plans(p).Select(x => x.Shot.Id).ToHashSet();
        Add(board is null || board.Frames.Count == 0 || !VideoStoryboard.Current(p), "CheckBoard", "storyboard");
        if (board is not null)
        {
            Add(!plans.SetEquals(board.Frames.Select(f => f.ShotId)), "CheckBoardPlans", "storyboard");
            Add(board.Frames.Any(f => f.ImageId != "" && p.References?.Images.Any(i => i.Id == f.ImageId) != true), "CheckBoardImages", "storyboard");
            Add(board.Frames.Any(f => f.ImageId == ""), "CheckNoImages", "storyboard", false);
            Add(board.Frames.Any(f => f.Estimated), "CheckEstimated", "storyboard", false);
            Add(board.Audio is not null && Math.Abs(VideoStoryboard.Duration(p) - board.Audio.Seconds) > .25, "CheckAudioLength", "storyboard", false);
        }
        return issues;
    }
    public static bool Ready(VideoPreparationProject p) => !Check(p).Any(i => i.Blocking);
    public static bool BoardReady(VideoPreparationProject p) => p.Storyboard is { Frames.Count: > 0 } b && VideoStoryboard.Current(p)
        && b.Frames.Select(f => f.ShotId).ToHashSet().SetEquals(VideoStoryboard.Plans(p).Select(x => x.Shot.Id))
        && b.Frames.All(f => f.ImageId == "" || p.References?.Images.Any(i => i.Id == f.ImageId) == true);
}
