using System.IO;
using System.Windows;
using System.Windows.Controls;
using AIHub.Services.OfficeEditor;
using AIHub.Controls.OfficeReader;
using Microsoft.Win32;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace AIHub.Controls.OfficeEditor;

public sealed partial class EditorWindow
{
    private void NewDocument() => Open(Owner, _l, _dark);
    private void SelectFile()
    {
        var picker = new OpenFileDialog { Multiselect = true, Filter = T("Files") + "|" + string.Join(";", EditorSession.Extensions.Select(e => "*" + e)) };
        if (picker.ShowDialog(this) == true) foreach (var path in picker.FileNames) Open(Owner, _l, _dark, path);
    }
    private async Task SaveAsync(bool saveAs = false)
    {
        if (!_ready || _closed) return;
        var path = _savePath;
        // Legacy imports are saved to an editable modern format, preserving the original.
        var extension = Path.GetExtension(path ?? "").ToLowerInvariant();
        if (saveAs || path is null || extension is not (OfficePackage.Extension or ".docx" or ".odt" or ".rtf"))
        {
            var picker = new SaveFileDialog { FileName = Path.GetFileNameWithoutExtension(path ?? T("NewDocument")), DefaultExt = ".LOPATAdoxAI",
                Filter = "LOPATA|*.LOPATAdoxAI|Word|*.docx|OpenDocument|*.odt|RTF|*.rtf", AddExtension = true, OverwritePrompt = true };
            if (picker.ShowDialog(this) != true) return; path = picker.FileName;
        }
        try
        {
            if (Path.GetExtension(path).Equals(OfficePackage.Extension, StringComparison.OrdinalIgnoreCase)) await _session.SavePackageAsync(path);
            else await ExportAsync(path);
            _savePath = path; _session.Draft.SetTarget(path); UpdateTitle(); _status.Text = T("Saved");
        }
        catch (Exception error) { Failure(error); }
    }
    private async Task ExportAsync(string path)
    {
        var full = Path.GetFullPath(path); var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await _session.RequestAsync(new { action = "export", path = temporary, kind = Path.GetExtension(full).TrimStart('.').ToLowerInvariant() });
            if (new FileInfo(temporary).Length == 0) throw new IOException("OfficeEditor.Failed");
            if (File.Exists(full)) File.Replace(temporary, full, full + ".bak"); else File.Move(temporary, full);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private async Task ExportPdfAsync()
    {
        if (!_ready) return;
        var picker = new SaveFileDialog { FileName = Path.GetFileNameWithoutExtension(_savePath ?? T("NewDocument")), Filter = "PDF|*.pdf", DefaultExt = ".pdf", AddExtension = true };
        if (picker.ShowDialog(this) == true) try { await ExportAsync(picker.FileName); _status.Text = T("Saved"); } catch (Exception error) { Failure(error); }
    }
    private async Task OpenReaderAsync()
    {
        if (!_ready) return;
        try
        {
            var snapshot = Path.Combine(_session.Draft.DirectoryPath, "reader-" + Guid.NewGuid().ToString("N") + ".LOPATAdoxAI");
            await _session.RequestAsync(new { action = "save" });
            OfficePackage.Save(snapshot, _session.Draft.Payload, _session.Provenance);
            ReaderWindowGroup.For(Owner, _l, _dark).Open(snapshot);
        }
        catch (Exception error) { Failure(error); }
    }
    private void ShowDrafts()
    {
        var drafts = EditorDraft.Recoverable();
        if (drafts.Count == 0) { System.Windows.MessageBox.Show(this, T("NoDrafts"), T("Drafts")); return; }
        var dialog = Dialog(T("Drafts"), out var panel); dialog.Width = 650;
        foreach (var draft in drafts.Take(50))
        {
            var label = (Path.GetFileName(draft.Target ?? draft.Source) ?? T("NewDocument")) + " · " + draft.UpdatedUtc.ToLocalTime().ToString("g");
            panel.Children.Add(Action(label, "Draft." + draft.Id, () => {
                var window = new EditorWindow(Owner, _l, _dark, null, draft.Id); window.Show(); dialog.Close();
            }));
        }
        dialog.ShowDialog();
    }
}
