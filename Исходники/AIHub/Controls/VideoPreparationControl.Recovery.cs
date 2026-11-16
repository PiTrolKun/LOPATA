using System.IO;
using AIHub.Services.VideoPreparation;

namespace AIHub.Controls;

public sealed partial class VideoPreparationControl
{
    internal void RestoreRecoveryProject(string? project, string? archive)
    {
        if (!Guid.TryParseExact(project, "N", out _) || Session.HasContent || ConceptBusy || PendingConcept) return;
        var useArchive = archive is not null && Path.IsPathFullyQualified(archive) && File.Exists(archive);
        var recovery = Path.Combine(Session.RecoveryRoot, project + VideoPreparationArchive.Extension);
        if (!useArchive && !File.Exists(recovery)) return;
        _autosave.Stop();
        Session.Load(useArchive ? archive! : recovery, recovery: !useArchive);
        ClearLocationError();
        _statusKey = "";
        Render();
    }
}
