using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using ProgressBar = System.Windows.Controls.ProgressBar;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VideoPreparation;

namespace AIHub.Controls;

public sealed partial class VideoPreparationControl
{
    public const string ExportKind = "video.preparation.export";
    public sealed record ExportRequest(string Id, string ProjectId, string ProjectPath, string Destination, string ArchiveSha256);
    private string? _lastExportPath;
    private ProgressBar? _exportProgress;
    private async Task ExportPreparationAsync()
    {
        if (ConceptBusy || PendingConcept) return;
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = T("ExportChooseFolder") };
        if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
        var directory = Path.Combine(picker.FolderName, "LOPATA_" + VideoPreparationExport.SafeName(Session.Project.Title)
            + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..6]);
        try { await ExportToAsync(directory); }
        catch (Exception e) { ConceptError(e); RefreshStatus(); }
    }
    public async Task ExportToAsync(string directory, CancellationToken token = default, BackgroundOperationState? restored = null)
    {
        if (ConceptBusy || PendingConcept && restored is null) return;
        if (!VideoPreparationReadiness.Ready(Session.Project)) { _statusKey = "PackageNotReady"; RefreshStatus(); return; }
        if (restored is null && !Save()) return;
        var p = Session.Project.Clone();
        using var archive = File.OpenRead(Session.ArchivePath!);
        var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
        var job = restored?.Input.Deserialize<ExportRequest>() ?? new(Guid.NewGuid().ToString("N"), p.Id, Session.ArchivePath!, Path.GetFullPath(directory), hash);
        if (job.ArchiveSha256 != hash) throw new BackgroundOperationWaitingException("VideoPrep.ExportChanged");
        _finishingBusy = true; _finishOperationKey = "ExportWorking"; _generatorPage?.SetWorkspaceBusy(true); _vacePage?.SetWorkspaceBusy(true);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token, ApplicationBackgroundOperations.ExitToken); _finishingCancel = cancel; Render();
        try
        {
            await ApplicationBackgroundOperations.RetireModelsAsync();
            var state = restored ?? new BackgroundOperationState { Id = job.Id, Kind = ExportKind, Title = T("ExportWorking"), Project = job.ProjectPath, Input = JsonSerializer.SerializeToElement(job) };
            var result = await ApplicationBackgroundOperations.RunAsync(ExportKind, state.Title, job.ProjectPath, job, async attempt =>
                await Task.Run(() => VideoPreparationExport.ExportAsync(p, job.Destination, attempt, new Progress<double>(value =>
                    Dispatcher.BeginInvoke(() => { if (_exportProgress is not null) { _exportProgress.IsIndeterminate = false; _exportProgress.Value = value; } })), job.Id), attempt), cancel.Token, state);
            _lastExportPath = result.Directory; _statusKey = "ExportDone";
        }
        catch (OperationCanceledException) { _statusKey = "FinishCanceled"; }
        catch (Exception e) { ConceptError(e); }
        finally { _finishingBusy = false; _finishingCancel = null; _generatorPage?.SetWorkspaceBusy(false); _vacePage?.SetWorkspaceBusy(false); Render(); }
    }
    public async Task ResumeExportAsync(BackgroundOperationState state, CancellationToken token)
    {
        var request = state.Input.Deserialize<ExportRequest>() ?? throw new InvalidDataException("VideoPrep.AiInvalidRequest");
        if (!OpenPath(request.ProjectPath) || Session.Project.Id != request.ProjectId) throw new BackgroundOperationWaitingException("VideoPrep.AiWrongProject");
        await ExportToAsync(request.Destination, token, state);
    }
    public bool ShowExportResult(BackgroundOperationState state)
    {
        var request = state.Input.Deserialize<ExportRequest>(); if (request is null || !Directory.Exists(request.Destination)) return false;
        if (Session.Project.Id != request.ProjectId && !OpenPath(request.ProjectPath)) return false;
        if (Session.Project.Id != request.ProjectId) return false;
        Session.Project.CurrentStep = "finish";
        _lastExportPath = request.Destination; _statusKey = "ExportDone"; Render(); return true;
    }
}
