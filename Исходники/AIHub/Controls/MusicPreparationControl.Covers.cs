using AIHub.Models;
using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class MusicPreparationControl
{
    private StorageSettings? _coverStorage;
    private Func<IMusicRhymeModel>? _coverModels;
    public void ConfigureCovers(StorageSettings storage, Func<IMusicRhymeModel> models)
    { _coverStorage = storage; _coverModels = models; ConfigureWorkspaceCovers(); }
    private void ConfigureWorkspaceCovers()
    { if (_workspace is not null && _coverStorage is not null && _coverModels is not null) _workspace.Covers.Configure(_coverStorage, _coverModels, _preparation.MaximumParallelConnections); }
    private bool CoverUsesArtifact(string id) => (_workspace?.Covers.Busy == true
        || ApplicationBackgroundOperations.Current?.FindOperation(MusicCoverWork.Kind)?.Phase is BackgroundOperationPhase.Running
            or BackgroundOperationPhase.Pausing or BackgroundOperationPhase.Paused or BackgroundOperationPhase.Waiting or BackgroundOperationPhase.Countdown
        || ApplicationBackgroundOperations.Current?.PausedOperations.Any(state => state.Kind == MusicCoverWork.Kind) == true)
        && (ImageGenerationCatalog.Get("z-image").Components.Contains(id) || id == ManagedModelCatalog.CoreArtifactId
            || id == HardwareRuntimeCatalog.SdCpuId || id == HardwareRuntimeCatalog.SdVulkanId);
    public Task ResumeCoverAsync(BackgroundOperationState state, CancellationToken token)
    { ShowWorkspace(); EnsureWorkspace(); return _workspace!.Covers.ResumeAsync(state, token); }
    public void ViewCoverResult(BackgroundOperationState state)
    { ShowWorkspace(); EnsureWorkspace(); _workspace!.Covers.ViewResult(state); }
}
