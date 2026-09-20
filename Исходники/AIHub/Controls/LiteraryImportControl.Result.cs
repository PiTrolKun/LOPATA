using System.Diagnostics;
using System.IO;
using System.Windows;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.LiteraryImport;

namespace AIHub.Controls;

public sealed partial class LiteraryImportControl
{
    private void ShowResult(LiteraryProjectEntry entry)
    {
        var complete=_session!.State.Stage=="complete";
        _body.Children.Clear(); _status.Text=L(complete?"Complete":"Partial");
        _body.Children.Add(LiteraryUi.Text(L("ResultHint")));
        _body.Children.Add(LiteraryUi.Text(ImportProjectStatus.Read(entry.ProjectPath).Describe(_l)));
        _body.Children.Add(LiteraryUi.Text(L("ResultStates")));
        _body.Children.Add(LiteraryUi.Button(L("Review"),()=>
        {
            var dialog=new LiteraryImportReviewWindow(Window.GetWindow(this),entry.ProjectPath,_l);
            dialog.ShowDialog();
            if(!dialog.Changed) return;
            _ = RunAsync(async ct=>
            {
                _runtime??=new LiteraryChatRuntime();
                await Task.Run(()=>ImportCompletion.CompleteAsync(_session!,entry,_runtime,_language,Progress(),ct),ct);
                ShowResult(entry);
            });
        }));
        _body.Children.Add(LiteraryUi.Button(L("ExportCurrent"),()=>_ = RunAsync(async ct=>
        { await Task.Run(()=>ImportCompletion.Export(_session!,entry,_language,ct),ct); ShowResult(entry); })));
        foreach(var (label,file) in new[]{("OpenBook","book.docx"),("OpenJournal","import-history.xlsx")})
            _body.Children.Add(LiteraryUi.Button(L(label),()=>Process.Start(new ProcessStartInfo(Path.Combine(entry.ProjectPath,"Exports","Import",file)) { UseShellExecute=true })));
        _body.Children.Add(LiteraryUi.Button(L("OpenFolder"),()=>Process.Start(new ProcessStartInfo(Path.Combine(entry.ProjectPath,"Exports","Import")) { UseShellExecute=true })));
        _body.Children.Add(LiteraryUi.Button(L("AssemblyOptions"),ShowWorks));
        _body.Children.Add(LiteraryUi.Button(L("OpenProject"),()=>
        { _session?.Dispose(); _session=null; OpenRequested?.Invoke(entry); },true));
        _runtime?.Dispose(); _runtime=null;
    }
}
