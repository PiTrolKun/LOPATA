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
using System.Windows;
using AIHub.Models.OneC;
using AIHub.Services.OneC;

namespace AIHub.Controls.OneC;

internal sealed partial class OneCControl
{
    private OneCEntryWindow Entry(string title, string name = "", bool code = false, bool feedback = false)
        => new(_owner, _l, T(title), name, code, feedback);
    private void ForgetWidgets()
    { _prompt = _platform = null; _contexts = null; _displayedProposal = null; _proposalEditor = null; }
    private async Task NewProjectAsync()
    {
        if (!await SaveDraftAsync()) return;
        var dialog = Entry("NewProject", T("DefaultProject"));
        if (dialog.ShowDialog() != true) return;
        var name = dialog.EntryName;
        Project = await Task.Run(() => Store.Create(name)); await RefreshProjectsAsync(); ForgetWidgets(); Render();
    }
    private async Task OpenProjectAsync()
    {
        if (_projects?.SelectedItem is not Choice item || !await SaveDraftAsync()) return;
        Project = await Task.Run(() => Store.Load(item.Id)); ForgetWidgets(); Render();
    }
    private async Task RenameProjectAsync()
    {
        if (Project is null || !await SaveDraftAsync()) return;
        var dialog = Entry("Rename", Project.Name);
        if (dialog.ShowDialog() != true) return;
        Project = Store.Update(Project.Id, p => p.Name = dialog.EntryName); await RefreshProjectsAsync(); Render();
    }
    private async Task NewTaskAsync()
    {
        if (Project is null || !await SaveDraftAsync()) return;
        Project = Store.Update(Project.Id, p =>
        {
            var task = new OneCTask(); p.Tasks.Add(task); p.ActiveTaskId = task.Id;
            p.ActiveProposalId = p.ActiveFileId = "";
        }); ForgetWidgets(); Render();
    }
    private async Task AddSourceAsync()
    {
        if (Project is null || !await SaveDraftAsync()) return;
        var dialog = Entry("AddSource", T("SourceName"), code: true);
        if (dialog.ShowDialog() != true) return;
        var file = Store.AddMaterial(Project, dialog.EntryName, dialog.Code);
        OneCProtocol.Bind(Project.Task, file); Project.ActiveFileId = ""; Store.Save(Project); Render();
    }
    private async Task ImportFileAsync()
    {
        if (Project is null || !await SaveDraftAsync()) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = T("ImportFilter"), Multiselect = true, CheckFileExists = true };
        if (dialog.ShowDialog(_owner) != true) return;
        var id = Project.Id;
        Busy(true);
        try
        {
            Project = await Task.Run(() =>
            {
                var p = Store.Load(id); OneCFile? first = null;
                foreach (var path in dialog.FileNames) { var imported = Import(path); first ??= imported; }
                if (first is not null) OneCProtocol.Bind(p.Task, first);
                p.ActiveFileId = ""; Store.Save(p); return p;
                OneCFile Import(string path) => Store.Import(p, path);
            }); Render();
        }
        finally { Busy(false); }
    }
    private async Task ImportFolderAsync()
    {
        if (Project is null || !await SaveDraftAsync()) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = T("ImportFolder"), Multiselect = false };
        if (dialog.ShowDialog(_owner) != true) return;
        var id = Project.Id; Busy(true);
        try
        {
            var folder = dialog.FolderName;
            var adapter = OneCAdapters.Sources.FirstOrDefault(a => a.Supports(folder)) ?? throw new InvalidDataException("OneC.UnsupportedFormat");
            Project = await Task.Run(async () =>
            {
                var sources = await adapter.ListAsync(folder, CancellationToken.None);
                var p = Store.Load(id); OneCFile? first = null;
                foreach (var source in sources) { var imported = Store.Import(p, source.Path); imported.Reference = source.RelativeName; first ??= imported; }
                if (first is null) throw new InvalidDataException("OneC.NoSourceFiles");
                OneCProtocol.Bind(p.Task, first); p.ActiveFileId = ""; Store.Save(p); return p;
            }); Render(); _status.Text = T("ImportedReadOnly");
        }
        finally { Busy(false); }
    }
    private async Task CreateBlankFileAsync()
    {
        if (Project is null || !await SaveDraftAsync()) return;
        var dialog = Entry("NewFile", T("ResultName"), code: true);
        if (dialog.ShowDialog() != true) return;
        var file = Store.CreateFile(Project, dialog.EntryName, dialog.Code);
        Project.OpenFileIds.Add(file.Id); Project.ActiveFileId = file.Id; Store.Save(Project); Render();
    }
    private async Task CreateResultAsync()
    {
        if (Project is null || _displayedProposal is null || !await SaveDraftAsync()) return;
        var proposal = Project.Proposals.Single(v => v.Id == _displayedProposal.Id);
        if (string.IsNullOrWhiteSpace(proposal.Code)) throw new InvalidDataException("OneC.MissingContext");
        var dialog = Entry("CreateResult", proposal.SuggestedName);
        if (dialog.ShowDialog() != true) return;
        var file = Store.CreateFromProposal(Project, proposal, dialog.EntryName);
        Project.ActiveFileId = file.Id; Project.OpenFileIds.Add(file.Id); Store.Save(Project); Render();
    }
    private async Task ApplyAsync()
    {
        if (Project is null || _displayedProposal is null || !await SaveDraftAsync()) return;
        var proposal = Project.Proposals.Single(v => v.Id == _displayedProposal.Id);
        var file = Store.Apply(Project, proposal);
        if (_editors.TryGetValue(EditorKey("file", file.Id), out var editor))
            editor.Editor.Document.Replace(0, editor.Editor.Document.TextLength, file.Code);
        Render(); _status.Text = T("Applied");
    }
    private async Task ErrorFeedbackAsync()
    {
        if (Project is null || _displayedProposal is null || !await SaveDraftAsync()) return;
        var proposalId = _displayedProposal.Id;
        var dialog = Entry("ErrorInOneC", feedback: true);
        if (dialog.ShowDialog() != true) return;
        Project = Store.Update(Project.Id, p =>
        {
            var proposal = p.Proposals.Single(v => v.Id == proposalId);
            p.Task.Feedback.Add(new(OneCIds.New(), proposalId, dialog.Message, dialog.Code, DateTimeOffset.UtcNow,
                OneCProjectStore.Hash(proposal.Code), proposal.Code));
            p.History.Add(new(OneCIds.New(), "userReportedError", proposalId, DateTimeOffset.UtcNow));
        }); Render(); _status.Text = T("FeedbackSaved");
    }
}
