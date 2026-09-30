namespace AIHub.Models;

public sealed class ApplicationBehaviorSettings
{
    public bool AskBeforeClosing { get; set; } = true;

    public bool CloseToTray { get; set; }

    // Reserved for a later implementation; the current UI cannot enable it.
    public bool LaunchWithWindows { get; set; }

    public string LastSettingsSection { get; set; } = "general";
}
