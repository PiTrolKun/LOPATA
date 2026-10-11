using AIHub.Models;
using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class MusicPreparationControl
{
    private MusicPoetryWindow? _embeddedPoetry;
    private bool _poetryOpen, _poetryWorking;
    public event Action? DirectPoetryRequested;

    public MusicProjectSnapshot CapturePoetrySeed()
    {
        if (_workspace is not null) return _workspace.Projects.Capture();
        var expert = MusicModelVariants.WorkspaceSettings(new MusicExpertSettings());
        return new MusicProjectSnapshot { Variation = expert.Variation, Expert = expert,
            Model = MusicModelVariants.Family(expert.Variation), ModelRevision = MusicModelVariants.Revision(expert.Variation) };
    }

    public void ShowPoetry(MusicPoetryWindow poetry)
    {
        if (_disposed) return;
        _models.PauseExamples(); _embeddedPoetry = poetry;
        poetry.PresentIn(this);
        WorkspaceChanged?.Invoke();
    }

    public void LeavePoetry(MusicPoetryWindow poetry)
    {
        if (_embeddedPoetry != poetry) return;
        _embeddedPoetry = null; IsWorkspace = false; IsModelSelection = true; Render();
    }

    public void SetPoetryState(bool open, bool working)
    {
        _poetryOpen = open; _poetryWorking = working;
        _workspace?.Generation.SetPoetryState(open, working);
    }
}
