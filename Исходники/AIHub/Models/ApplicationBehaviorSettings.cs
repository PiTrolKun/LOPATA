namespace AIHub.Models;

public sealed class ApplicationBehaviorSettings
{
    public bool AskBeforeClosing { get; set; } = true;

    public bool CloseToTray { get; set; }

    public bool LaunchWithWindows { get; set; }

    public bool AutoResumeBackgroundOperation { get; set; } = true;

    public bool ComponentAuditEnabled { get; set; } = true;

    public bool ComponentAuditEverDisabled { get; set; }

    public bool ImageShellIntegrationEnabled { get; set; } = true;

    public bool VectorEditorEnabled { get; set; } = true;

    public bool VectorEditorInsertOnCanvas { get; set; }

    // null migrates the old utility's existing registration once; new installations remain off.
    public bool? BookShellIntegrationEnabled { get; set; }

    public string LastSettingsSection { get; set; } = "general";
}
