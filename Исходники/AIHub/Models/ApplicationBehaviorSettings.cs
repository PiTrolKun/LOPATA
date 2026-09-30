namespace AIHub.Models;

public sealed class ApplicationBehaviorSettings
{
    public bool AskBeforeClosing { get; set; } = true;

    public bool CloseToTray { get; set; }

    public bool LaunchWithWindows { get; set; }

    public bool AutoResumeBackgroundOperation { get; set; } = true;

    public string LastSettingsSection { get; set; } = "general";
}
