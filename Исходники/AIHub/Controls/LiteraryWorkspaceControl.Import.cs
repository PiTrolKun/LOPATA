using System.IO;
using AIHub.Services.LiteraryImport;

namespace AIHub.Controls;

public sealed partial class LiteraryWorkspaceControl
{
    private async Task RefreshImportExportAsync()
    {
        if(!File.Exists(Path.Combine(_entry.ProjectPath,"Import","origin.json"))) return;
        // Rebuild the summary from disk instead of appending to a previous failure.
        var summary = "";
        try
        {
            var status = await Task.Run(() => ImportProjectStatus.Read(_entry.ProjectPath));
            summary = status.Describe(_l);
            _memoryStatus.Text = summary;
            // The staged importer retains its accepted book as an archive. Workspace exports
            // use the editor's normal export action; the legacy importer must not reset its stage.
            var handoff = Path.Combine(_entry.ProjectPath,"Import","workspace.json");
            if (File.Exists(handoff))
            {
                using var state = System.Text.Json.JsonDocument.Parse(File.ReadAllText(handoff));
                if (state.RootElement.GetProperty("Ready").GetBoolean()) return;
            }
            await Task.Run(()=>ImportProjectStatus.ExportCurrent(_entry.ProjectPath,_project.LanguageCode,CancellationToken.None));
            _memoryStatus.Text=summary+"\n"+_l("Literary.Import.ExportRefreshed");
        }
        catch(Exception ex)
        {
            _memoryStatus.Text=(summary.Length>0?summary+"\n":"")+_l("Literary.Import.ExportRetry")+" "+(ex.Message.StartsWith("Literary.")?_l(ex.Message):ex.Message);
        }
    }
}
