namespace AIHub.Services.VideoEditor;

/// <summary>Keep only the current narrator alive while sequential fragments are generated.</summary>
internal sealed class VideoNarratorBatch(VideoNarratorRegistry registry) : IDisposable
{
    private string? _id;
    private IVideoNarrator? _active;
    internal IVideoNarrator Get(string id)
    {
        if (_active is not null && _id == id) return _active;
        _active?.Dispose(); _active = null; _id = null;
        _active = registry.Create(id); _id = id; return _active;
    }
    public void Dispose() { _active?.Dispose(); _active = null; _id = null; }
}
