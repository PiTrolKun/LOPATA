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
    private Task<string?> PrepareUpscaleModels(System.Windows.Window owner, UpscaleProject project, CancellationToken token)
    {
        var root = _storageSettings.Models.Locations.Select(l => l.Path?.Trim()).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)) ?? "";
        if (project.Steps.All(s => !s.Enabled || s.Engine == UpscaleEngine.Scale)) return Task.FromResult<string?>(root);
        using var preparation = new UpscaleComponents(_appSettings.ModelDownloads?.MaximumParallelConnections ?? 4);
        using var audit = ComponentAudit.CaptureOperation();
        Task<bool> Run(bool download, IProgress<AIHub.Models.ManagedModelDownloadProgress> progress, CancellationToken ct) => preparation.PrepareAsync(root, project, download, progress, ct);
        var cards = UpscaleComponents.Cards(root, project);
        var details = string.Join(Environment.NewLine, cards.Select(c => $"{c.DisplayName}: {c.TotalBytes / 1073741824d:0.00} GiB"));
        var names = cards.ToDictionary(c => c.ModelArtifactId, c => c.DisplayName);
        return Task.FromResult(new VideoPreparationWindow(owner, L, L("VideoUpscale.Components"), Run, token, details, names)
            .ShowDialog() == true ? root : null);
    }
    private void RegisterUpscaleOperations()
    {
        _backgroundOperations!.Register(UpscalePipeline.Kind, async (state, token) => {
            var checkpoint = state.Input.Deserialize<UpscaleCheckpoint>() ?? throw new InvalidDataException("VideoUpscale.InvalidProject");
            var full = Path.GetFullPath(checkpoint.JobFile);
            if (!UpscaleStore.IsJobPath(full)) throw new InvalidDataException("VideoUpscale.InvalidProject");
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
            if (!UpscaleStore.IsJobPath(full)) throw new InvalidDataException("VideoUpscale.InvalidProject");
            var job = UpscaleStore.Load<UpscaleJob>(full); OpenUpscaleUtility(job.Project);
            if (_upscaleWindow is not {} window) return;
            await window.Ready.Task.WaitAsync(token);
            if (window.Project.Id != job.Project.Id) await window.RestoreProject(job.Project);
            await window.ExecutePartial(job, token, state);
        });
    }
}
