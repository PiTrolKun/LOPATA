using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MenuItem = System.Windows.Controls.MenuItem;
using ContextMenu = System.Windows.Controls.ContextMenu;
using Control = System.Windows.Controls.Control;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Panel = System.Windows.Controls.Panel;
using CheckBox = System.Windows.Controls.CheckBox;
using ProgressBar = System.Windows.Controls.ProgressBar;
using TabControl = System.Windows.Controls.TabControl;
using ComboBox = System.Windows.Controls.ComboBox;
using System.IO;
using AIHub.Models.OneC;
using AIHub.Services.OneC;

namespace AIHub.Controls.OneC;

internal sealed partial class OneCControl
{
    private sealed record FileDraft(string Id, string Code, bool Writable, int Caret, int Selection, double Scroll);
    private sealed record Draft(string ProjectId, string TaskId, long Version, string Prompt, string Mode, string Context, string Platform,
        string SourceId, string SourceHash, int Start, int Length, bool Whole, string ActiveFile, string[] OpenFiles, bool Sidebar,
        string ActiveProposal, string? ProposalId, string? ProposalCode, FileDraft[] Files);
    private string EditorKey(string type, string id) => Project!.Id + ":" + type + ":" + id;
    private Draft CaptureDraft()
    {
        var p = Project ?? throw new InvalidOperationException("OneC.InvalidProject");
        var task = p.Task;
        var files = new List<FileDraft>();
        foreach (var file in p.Files)
            if (_editors.TryGetValue(EditorKey("file", file.Id), out var editor))
                files.Add(new(file.Id, editor.Text, !editor.ReadOnly, editor.SelectionStart, editor.SelectionLength, editor.Editor.VerticalOffset));
        return new(p.Id, task.Id, _draftVersion, _prompt?.Text ?? task.Prompt, _mode,
            (_contexts?.SelectedItem as Choice)?.Id ?? task.Context, _platform?.Text ?? task.PlatformVersion,
            task.SourceFileId, task.SourceHash, task.Start, task.Length, task.WholeFile, p.ActiveFileId, p.OpenFileIds.ToArray(), p.SidebarVisible,
            p.ActiveProposalId, _displayedProposal?.Id, _proposalEditor?.Text, files.ToArray());
    }
    private OneCProject ApplyDraft(Draft d, bool preserveConflicts = false)
    {
        lock (_draftGate)
        {
            if (_savedVersions.GetValueOrDefault(d.ProjectId, -1) > d.Version) return Store.Load(d.ProjectId);
            var result = Store.Update(d.ProjectId, fresh =>
            {
                var task = fresh.Tasks.Single(t => t.Id == d.TaskId);
                task.Prompt = d.Prompt; task.Mode = d.Mode; task.Context = d.Context; task.PlatformVersion = d.Platform;
                task.SourceFileId = d.SourceId; task.SourceHash = d.SourceHash; task.Start = d.Start; task.Length = d.Length; task.WholeFile = d.Whole;
                fresh.ActiveTaskId = task.Id; fresh.ActiveFileId = d.ActiveFile; fresh.OpenFileIds = d.OpenFiles.ToList(); fresh.SidebarVisible = d.Sidebar;
                fresh.ActiveProposalId = d.ActiveProposal;
                if (d.ProposalId is { } id && d.ProposalCode is { } code && fresh.Proposals.SingleOrDefault(v => v.Id == id) is { } proposal)
                    proposal.Code = code;
                foreach (var draft in d.Files)
                {
                    var file = fresh.Files.Single(f => f.Id == draft.Id);
                    if (draft.Writable)
                    {
                        if (preserveConflicts) { if (file.Code != draft.Code) file.RecoveryCode = draft.Code; }
                        else
                        {
                            if (!Store.CanEdit(fresh, file)) throw new InvalidDataException("OneC.ReadOnly");
                            file.Code = draft.Code; file.RecoveryCode = "";
                        }
                    }
                    file.Caret = draft.Caret; file.SelectionLength = draft.Selection; file.Scroll = draft.Scroll;
                }
            });
            _savedVersions[d.ProjectId] = d.Version; return result;
        }
    }
    private async Task<bool> SaveDraftAsync(bool show = false)
    {
        if (Project is null || _rendering) return true;
        _saveTimer.Stop();
        Draft? snapshot = null;
        try
        {
            snapshot = CaptureDraft();
            var updated = await Task.Run(() => ApplyDraft(snapshot));
            if (Project?.Id == snapshot.ProjectId && _draftVersion == snapshot.Version)
            {
                Project = updated; _saveState.Text = T("Saved");
                if (show) _status.Text = T("SavedAll");
            }
            return true;
        }
        catch (Exception error)
        {
            _saveState.Text = T("SaveFailed"); Error(error);
            if (snapshot is not null && error.Message is "OneC.FileChanged" or "OneC.ReadOnly")
                try { await Task.Run(() => ApplyDraft(snapshot, preserveConflicts: true)); _saveState.Text = T("ConflictDraftSaved"); }
                catch (Exception recovery) { Error(recovery); }
            return false;
        }
    }
    internal bool Checkpoint()
    {
        if (Project is null) return true;
        _saveTimer.Stop();
        var snapshot = CaptureDraft();
        try { Project = ApplyDraft(snapshot); return true; }
        catch (Exception error)
        {
            Error(error);
            if (error.Message is "OneC.FileChanged" or "OneC.ReadOnly")
                try { ApplyDraft(snapshot, preserveConflicts: true); _saveState.Text = T("ConflictDraftSaved"); } catch (Exception recovery) { Error(recovery); }
            return false;
        }
    }
    private OneCCodeEditor FileEditor(OneCFile file)
    {
        var key = EditorKey("file", file.Id);
        if (!_editors.TryGetValue(key, out var editor))
        {
            editor = new(_l, "OneC.File." + file.Id); editor.SetText(file.Code);
            editor.ReadOnly = !Store.CanEdit(Project!, file);
            editor.Changed += Dirty; editor.SelectionChanged += Dirty;
            editor.UseInTask += async () => await UseFileAsync(file.Id, editor);
            _editors.Add(key, editor);
            editor.Loaded += (_, _) => { if (Project?.Files.SingleOrDefault(f => f.Id == file.Id) is { } current) editor.Restore(current.Caret, current.SelectionLength, current.Scroll); };
        }
        return editor;
    }
    private OneCCodeEditor ResultEditor(OneCProposal proposal)
    {
        var key = EditorKey("proposal", proposal.Id);
        if (!_editors.TryGetValue(key, out var editor))
        {
            editor = new(_l, "OneC.Proposal." + proposal.Id); editor.SetText(proposal.Code);
            editor.Changed += () => { Dirty(); RefreshComparison(); }; _editors.Add(key, editor);
        }
        return editor;
    }
    private async Task OpenFileAsync(string id)
    {
        if (Project is null || !await SaveDraftAsync()) return;
        if (!Project.OpenFileIds.Contains(id)) Project.OpenFileIds.Add(id);
        Project.ActiveFileId = id; Dirty(); await SaveDraftAsync(); Render();
    }
    private async Task UseFileAsync(string id, OneCCodeEditor editor)
    {
        if (Project is null || !await SaveDraftAsync()) return;
        var file = Project.Files.Single(f => f.Id == id);
        OneCProtocol.Bind(Project.Task, file, editor.SelectionStart, editor.SelectionLength, editor.SelectionLength == 0);
        Project.ActiveFileId = ""; Dirty(); await SaveDraftAsync(); Render();
    }
}
