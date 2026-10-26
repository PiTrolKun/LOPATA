namespace AIHub.Services.VideoEditor;

internal readonly record struct VideoItemId(string Id, bool Subtitle = false);

internal sealed class VideoSelection
{
    private readonly HashSet<VideoItemId> _items = [];
    private readonly HashSet<VideoItemId> _roots = [];
    internal Func<IReadOnlyCollection<VideoItemId>, VideoItemId[]>? Resolve { get; set; }
    internal IReadOnlyCollection<VideoItemId> Roots => _roots;
    internal IReadOnlyCollection<VideoItemId> Items => _items;
    internal VideoItemId? Primary { get; private set; }
    internal int Count => _items.Count;
    internal bool Contains(VideoItemId item) => _items.Contains(item);
    internal void Replace(IEnumerable<VideoItemId> items, VideoItemId? primary = null)
    {
        var snapshot = items.ToArray(); _roots.Clear(); _roots.UnionWith(snapshot); Rebuild(); Primary = primary is {} p && _items.Contains(p) ? p : _roots.Count > 0 ? _roots.Last() : null;
    }
    internal void Toggle(VideoItemId item)
    {
        if (_items.Contains(item)) _roots.RemoveWhere(root => root == item || Resolve?.Invoke([root]).Contains(item) == true);
        else _roots.Add(item);
        Rebuild(); Primary = _items.Contains(item) ? item : _roots.Count > 0 ? _roots.Last() : null;
    }
    internal void Activate(VideoItemId item) { if (_items.Contains(item)) Primary = item; else Replace([item]); }
    private void Rebuild() { _items.Clear(); _items.UnionWith(Resolve?.Invoke(_roots) ?? _roots.ToArray()); }
    internal void Prune(VideoProject p) => Replace(_roots.Where(i => i.Subtitle ? p.Narration.Any(n => n.Id == i.Id) : p.Tracks.Any(t => t.Clips.Any(c => c.Id == i.Id))).ToArray(), Primary);
}
