using System.Diagnostics;
using System.IO;
using AIHub.Services.OneC;

namespace AIHub.Controls.OneC;

internal sealed partial class OneCControl
{
    private async Task ExportResultAsync()
    {
        if (Project is null || _displayedProposal is null || !await SaveDraftAsync()) return;
        var proposal = Project.Proposals.Single(v => v.Id == _displayedProposal.Id);
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = proposal.SuggestedName, Filter = T("ExportFilter"),
            AddExtension = true, DefaultExt = ".bsl", OverwritePrompt = false };
        if (dialog.ShowDialog(_owner) != true) return;
        await Task.Run(() => OneCExport.SaveNew(dialog.FileName, OneCProjectStore.CompleteCode(proposal), proposal.Models));
        _status.Text = T("Exported");
    }
    private Task ShowOwnedFileAsync(string id)
    {
        if (Project is null) return Task.CompletedTask;
        var file = Project.Files.Single(f => f.Id == id);
        if (!Store.CanEdit(Project, file)) return Task.CompletedTask;
        Store.VerifyDisk(Project, file);
        var path = Store.FileFor(Project.Id, file.DiskPath);
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", path }, UseShellExecute = true });
        return Task.CompletedTask;
    }
}
