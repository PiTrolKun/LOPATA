using AIHub.Models;
using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class MusicPreparationControl
{
    private StorageSettings? _rhymeStorage;
    private Func<IMusicRhymeModel>? _rhymeModels;
    public void ConfigureRhyme(StorageSettings storage, Func<IMusicRhymeModel> models)
    { _rhymeStorage = storage; _rhymeModels = models; ConfigureWorkspaceRhyme(); }
    private void ConfigureWorkspaceRhyme()
    { if (_workspace is not null && _rhymeStorage is not null && _rhymeModels is not null) _workspace.Rhyme.Configure(_rhymeStorage, _rhymeModels); }
    public Task ResumeRhymeAsync(BackgroundOperationState state, CancellationToken token)
    { ShowWorkspace(); EnsureWorkspace(); return _workspace!.Rhyme.ResumeAsync(state, token); }
    public void ViewRhymeResult(string? project)
    { ShowWorkspace(); EnsureWorkspace(); _workspace!.Rhyme.Open(project: project); }
}
