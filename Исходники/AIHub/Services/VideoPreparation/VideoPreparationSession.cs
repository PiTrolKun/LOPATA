using System.IO;

namespace AIHub.Services.VideoPreparation;

/// <summary>Per-project save state; an internal recovery checkpoint is never reported as the user's saved archive.</summary>
public sealed class VideoPreparationSession(string? recoveryRoot = null)
{
    public string RecoveryRoot { get; } = recoveryRoot ?? Path.Combine(AppDataPaths.BaseDirectory, "VideoPreparation", "Recovery");
    public VideoPreparationProject Project { get; private set; } = new();
    public string? ArchivePath { get; private set; }
    public bool IsDirty { get; private set; }
    public bool RecoveryCurrent { get; private set; }
    public bool HasContent => IsDirty || ArchivePath is not null || Project.Title.Length != 0 || Project.Form.Length != 0 || Project.IdeaText.Length != 0;
    public string RecoveryPath => Path.Combine(RecoveryRoot, Project.Id + VideoPreparationArchive.Extension);

    public void Changed()
    {
        Project.ModifiedUtc = DateTimeOffset.UtcNow;
        IsDirty = true; RecoveryCurrent = false;
    }
    public void Checkpoint(bool userArchive = true)
    {
        if (!IsDirty) return;
        VideoDevelopment.Capture(Project); VideoReferencePlans.Capture(Project); VideoShots.Capture(Project); VideoStoryboard.Capture(Project);
        if (!RecoveryCurrent) { VideoPreparationArchive.Save(Project, RecoveryPath); RecoveryCurrent = true; }
        if (userArchive && ArchivePath is not null) { VideoPreparationArchive.Save(Project, ArchivePath); IsDirty = false; }
    }
    public void Save(string path)
    {
        VideoDevelopment.Capture(Project); VideoReferencePlans.Capture(Project); VideoShots.Capture(Project); VideoStoryboard.Capture(Project);
        var full = Path.GetFullPath(path);
        if (!RecoveryCurrent) { VideoPreparationArchive.Save(Project, RecoveryPath); RecoveryCurrent = true; }
        VideoPreparationArchive.Save(Project, full);
        ArchivePath = full; IsDirty = false;
    }
    public void Load(string path, bool recovery = false)
    {
        var loaded = VideoPreparationArchive.Load(path, Path.Combine(RecoveryRoot, "Opened"));
        ApplyLoaded(loaded, path, recovery);
    }
    public void ApplyLoaded(VideoPreparationProject loaded, string path, bool recovery = false)
    {
        loaded.Validate();
        VideoDevelopment.Capture(loaded);
        Project = loaded; ArchivePath = recovery ? null : Path.GetFullPath(path);
        IsDirty = recovery; RecoveryCurrent = false;
    }
    public void New()
    {
        Project = new(); ArchivePath = null; IsDirty = RecoveryCurrent = false;
    }
    public string[] Recoverable() => Directory.Exists(RecoveryRoot)
        ? Directory.GetFiles(RecoveryRoot, "*" + VideoPreparationArchive.Extension).OrderByDescending(File.GetLastWriteTimeUtc).ToArray() : [];
}
