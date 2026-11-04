using System.IO;
using System.Text.Json;
using AIHub.Controls.VideoEditor;
using AIHub.Controls.VideoUpscale;
using AIHub.Services;
using AIHub.Services.VideoUpscale;

namespace AIHub;

public partial class MainWindow
{
    private UpscaleWindow? _upscaleWindow;
    private void OpenUpscaleUtility(UpscaleProject? project = null)
    {
        if (_upscaleWindow is {} existing) { existing.Show(); existing.Activate(); return; }
        var window = new UpscaleWindow(this, L, project) { EnsureRuntime = EnsureApplicationStartupAsync };
        window.PrepareModels = (p, ct) => PrepareUpscaleModels(window, p, ct);
        window.Closed += (_, _) => { if (_upscaleWindow == window) _upscaleWindow = null; };
        _upscaleWindow = window; window.Show();
    }
    private async Task<string?> PrepareUpscaleModels(System.Windows.Window owner, UpscaleProject project, CancellationToken token)
    {
        var root = _storageSettings.Models.Locations.Select(l => l.Path?.Trim()).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)) ?? "";
        if (project.Steps.All(s => !s.Enabled || s.Engine == UpscaleEngine.Scale)) return root;
        using var preparation = new UpscaleComponents(_appSettings.ModelDownloads?.MaximumParallelConnections ?? 4);
        using var audit = ComponentAudit.CaptureOperation();
        Task<bool> Run(bool download, IProgress<AIHub.Models.ManagedModelDownloadProgress> progress, CancellationToken ct) => preparation.PrepareAsync(root, project, download, progress, ct);
        if (await Run(false, new Progress<AIHub.Models.ManagedModelDownloadProgress>(), token)) return root;
        var details = string.Join(Environment.NewLine, UpscaleComponents.Cards(root, project).Select(c => $"{c.DisplayName}: {c.TotalBytes / 1073741824d:0.00} GiB"));
        return new VideoPreparationWindow(owner, L, L("VideoUpscale.Components"), Run, token, details).ShowDialog() == true ? root : null;
    }
    private void RegisterUpscaleOperations()
    {
        _backgroundOperations!.Register(UpscalePipeline.Kind, async (state, token) => {
            var checkpoint = state.Input.Deserialize<UpscaleCheckpoint>() ?? throw new InvalidDataException("VideoUpscale.InvalidProject");
            var full = Path.GetFullPath(checkpoint.JobFile);
            if (!full.StartsWith(Path.GetFullPath(UpscaleStore.WorkRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("VideoUpscale.InvalidProject");
            var job = UpscaleStore.Load<UpscaleJob>(full); OpenUpscaleUtility(job.Project);
            if (_upscaleWindow is not {} window) return;
            await window.Ready.Task.WaitAsync(token);
            if (window.Project.Id != job.Project.Id) await window.RestoreProject(job.Project);
            var root = await PrepareUpscaleModels(window, job.Project, token);
            if (root is null) throw new OperationCanceledException(token);
            await window.ExecuteJob(job, root, token, state);
        });
        _backgroundOperations.Register(UpscaleAssembly.PartialKind, async (state, token) => {
            var checkpoint = state.Input.Deserialize<UpscaleCheckpoint>() ?? throw new InvalidDataException("VideoUpscale.InvalidProject");
            var full = Path.GetFullPath(checkpoint.JobFile);
            if (!full.StartsWith(Path.GetFullPath(UpscaleStore.WorkRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("VideoUpscale.InvalidProject");
            var job = UpscaleStore.Load<UpscaleJob>(full); OpenUpscaleUtility(job.Project);
            if (_upscaleWindow is not {} window) return;
            await window.Ready.Task.WaitAsync(token);
            if (window.Project.Id != job.Project.Id) await window.RestoreProject(job.Project);
            await window.ExecutePartial(job, token, state);
        });
    }
}
