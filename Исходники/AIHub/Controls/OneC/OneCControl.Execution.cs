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
using System.Windows.Media;
using AIHub.Controls.OfficeReader;
using AIHub.Controls.VideoEditor;
using AIHub.Models;
using AIHub.Models.OneC;
using AIHub.Services;
using AIHub.Services.OneC;
using AIHub.Services.OfficeReader;

namespace AIHub.Controls.OneC;

internal sealed partial class OneCControl
{
    private string _activeJobId = "", _discardJobId = "";
    internal Task CancelProcessingAsync()
    {
        if (_activeJobId.Length > 0) _discardJobId = _activeJobId;
        _cancel?.Cancel();
        if (!_busy && Project?.Pending is { } pending)
        {
            ApplicationBackgroundOperations.Current?.DiscardPending(pending.Id);
            Project = Store.Update(Project.Id, p => p.Pending = null); Render(); _status.Text = T("Canceled");
        }
        return Task.CompletedTask;
    }
    private ReaderAiSettingsStore ModelSettings => new(Path.Combine(AppDataPaths.BaseDirectory, "OneC", "ai-settings.json"));
    private async Task ChooseModelAsync()
    {
        if (Project is null || !await SaveDraftAsync()) return;
        var settings = ModelSettings;
        if (Project.ModelId.Length > 0) { var value = settings.Load(); value.TextModel = Project.ModelId; settings.Save(value); }
        var dark = TryFindResource("TextPrimaryBrush") is SolidColorBrush b && b.Color.R > 160;
        if (new ReaderAiWindow(_owner, _l, settings, textOnly: true, darkTheme: dark, title: T("ModelTitle"), hint: T("ModelHint")).ShowDialog() != true) return;
        var id = settings.Load().TextModel;
        Project = Store.Update(Project.Id, p => p.ModelId = id); Render();
    }
    private Task PrepareCheckerAsync() { if (EnsureChecker()) _status.Text = T("CheckerReady"); return Task.CompletedTask; }
    private bool EnsureTextModel()
    {
        if (_modelFactory is not null) return true;
        var library = new ManagedModelLibraryStore(); var storage = new StorageSettingsStore().LoadOrCreate();
        var model = ReaderAiModelCatalog.TextModels(storage, library.LoadAll()).SingleOrDefault(m => m.Id == Project!.ModelId)
            ?? throw new InvalidDataException("OneC.NoModel");
        using var preparation = new ReaderTextPreparation(library);
        preparation.ParallelConnections = new AppSettingsStore().LoadOrCreate().ModelDownloads.MaximumParallelConnections;
        return new VideoPreparationWindow(_owner, _l, model.Name,
            (download, progress, token) => preparation.PrepareAsync(model.Card, download, progress, token), CancellationToken.None).ShowDialog() == true;
    }
    private bool EnsureChecker()
    {
        if (_checker is not null) return true;
        var runtime = new OneCBslRuntime();
        async Task<bool> Prepare(bool download, IProgress<ManagedModelDownloadProgress> progress, CancellationToken token)
        {
            if (!runtime.Present && !download) return false;
            await runtime.PrepareAsync(download, new Progress<ComponentDownloadProgress>(p => progress.Report(new(p.ComponentId,
                "BSL Language Server", p.DownloadedBytes, p.TotalBytes, p.BytesPerSecond, p.Stage))), token);
            return true;
        }
        return new VideoPreparationWindow(_owner, _l, "BSL Language Server " + OneCBslRuntime.Version, Prepare,
            CancellationToken.None, T("CheckerPreparationHint")).ShowDialog() == true;
    }
    internal async Task GenerateAsync()
    {
        using var audit = ComponentAudit.CaptureOperation();
        if (Project is null || !await SaveDraftAsync()) return;
        if (Project.Pending is not null) { _status.Text = T("Pending"); return; }
        if (Project.ModelId.Length == 0) { await ChooseModelAsync(); if (Project.ModelId.Length == 0) return; }
        if (!EnsureTextModel() || !EnsureChecker()) return;
        Project = Store.Update(Project.Id, p => p.Pending = OneCProtocol.CreateJob(p, p.ModelId, UiLanguage, p.ActiveProposalId));
        await RunPendingAsync();
    }
    internal async Task ResumeAsync(BackgroundOperationState state, CancellationToken token)
    {
        var pointer = OneCWork.ReadPointer(state);
        await EnterAsync(pointer.ProjectId);
        if (Project?.Id != pointer.ProjectId) throw new InvalidDataException("OneC.InvalidProject");
        await RunPendingAsync(state, token);
    }
    private async Task RunPendingAsync(BackgroundOperationState? restored = null, CancellationToken token = default)
    {
        if (_busy || Project?.Pending is not { } job) return;
        var projectId = Project.Id; Busy(true); _cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        _activeJobId = job.Id; _discardJobId = "";
        try
        {
            var worker = new OneCWork(Store, _modelFactory, _checker);
            var result = await Task.Run(() => worker.RunAsync(projectId, job.Id, T("Title") + " · " + Project.Name,
                new Progress<string>(key => Dispatcher.InvokeAsync(() => _status.Text = _l(key))), _cancel.Token, restored));
            await SaveDraftAsync();
            Project = Store.Update(projectId, p => { p.ActiveTaskId = job.TaskId; p.ActiveProposalId = result; p.ActiveFileId = ""; });
            ForgetWidgets(); Render(); _status.Text = T("Completed");
        }
        catch (OperationCanceledException) { _status.Text = T("Canceled"); }
        finally
        {
            _cancel.Dispose(); _cancel = null; Busy(false);
            _activeJobId = "";
            if (_discardJobId == job.Id)
                Store.Update(projectId, p => { if (p.Pending?.Id == job.Id) p.Pending = null; });
            _discardJobId = "";
            if (Project?.Id == projectId) { await SaveDraftAsync(); Project = Store.Load(projectId); Render(); }
        }
    }
    private async Task PauseAsync()
    {
        if (ApplicationBackgroundOperations.Current is { } current && current.State?.Kind == OneCWork.Kind)
        { await current.PauseAsync(); _status.Text = T("Paused"); }
    }
    private async Task ResumePendingAsync()
    {
        if (ApplicationBackgroundOperations.Current is { } current && current.State?.Kind == OneCWork.Kind && current.HasPending)
        { await current.ResumeAsync(CancellationToken.None); return; }
        if (Project is null || !await SaveDraftAsync() || Project.Pending is null) return;
        if (EnsureTextModel() && EnsureChecker()) await RunPendingAsync();
    }
    private async Task CheckProposalAsync()
    {
        if (Project is null || _displayedProposal is null || !await SaveDraftAsync() || !EnsureChecker()) return;
        var id = Project.Id; var proposal = Project.Proposals.Single(v => v.Id == _displayedProposal.Id);
        var code = OneCProjectStore.CompleteCode(proposal);
        using var audit = ComponentAudit.CaptureOperation(); using var lease = ApplicationBackgroundOperations.Current?.BeginAuxiliary();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ApplicationBackgroundOperations.ExitToken); _cancel = cancel; Busy(true);
        try
        {
            _status.Text = T("Checking");
            var check = await Task.Run(() => (_checker ?? OneCAdapters.CreateChecker()).CheckAsync(code, proposal.SuggestedName, UiLanguage, cancel.Token));
            await SaveDraftAsync(); var updated = Store.Update(id, p => p.Proposals.Single(v => v.Id == proposal.Id).Check = check);
            if (Project?.Id == id)
            {
                Project = updated;
                if (_displayedProposal?.Id == proposal.Id) { _displayedProposal = updated.Proposals.Single(v => v.Id == proposal.Id); RefreshCheckNotes(); RefreshComparison(); }
                _status.Text = CheckSummary(check);
            }
            ShowCheckWindow(proposal.SuggestedName, code, check);
        }
        finally { _cancel = null; Busy(false); }
    }
    private async Task CheckFileAsync(string fileId)
    {
        if (Project is null || !await SaveDraftAsync() || !EnsureChecker()) return;
        var id = Project.Id; var file = Project.Files.Single(f => f.Id == fileId);
        var code = file.Code;
        using var audit = ComponentAudit.CaptureOperation(); using var lease = ApplicationBackgroundOperations.Current?.BeginAuxiliary();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ApplicationBackgroundOperations.ExitToken); _cancel = cancel; Busy(true);
        try
        {
            _status.Text = T("Checking");
            var check = await Task.Run(() => (_checker ?? OneCAdapters.CreateChecker()).CheckAsync(code, file.Name, UiLanguage, cancel.Token));
            await SaveDraftAsync(); var updated = Store.Update(id, p => p.Files.Single(f => f.Id == fileId).Check = check);
            if (Project?.Id == id) { Project = updated; _status.Text = CheckSummary(check); }
            ShowCheckWindow(file.Name, code, check);
        }
        finally { _cancel = null; Busy(false); }
    }
}
