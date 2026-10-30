using AIHub.Services.VideoPreparation;

namespace AIHub.Controls;

public sealed partial class VideoPreparationControl
{
    private VideoLtxPreparationControl? _ltxPreparationPage;

    internal VideoLtxPreparationControl ShowLtxPreparation(string root, int connections,
        Func<IVideoLtxPreparation>? create = null)
    {
        var page = new VideoLtxPreparationControl(_localize, root, connections, create);
        page.BackRequested += () =>
        {
            if (!ReferenceEquals(page, _ltxPreparationPage)) return;
            _ltxPreparationPage = null;
            Render();
        };
        page.HomeRequested += () =>
        {
            if (ReferenceEquals(page, _ltxPreparationPage) && Checkpoint()) HomeRequested?.Invoke();
        };
        _ltxPreparationPage = page;
        Render();
        return page;
    }
}
