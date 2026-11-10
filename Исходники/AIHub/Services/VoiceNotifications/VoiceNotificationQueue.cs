namespace AIHub.Services.VoiceNotifications;

internal sealed record VoiceNotice(string EventId, string ClipId, int Group, string Scope,
    string[] Contexts, string Family = "", bool Terminal = false, string Occurrence = "")
{
    internal string Key => EventId + "\n" + Scope + "\n" + Occurrence;
}

/// <summary>Only pending speech lives here; history and the playing phrase are not queue entries.</summary>
internal sealed class VoiceNotificationQueue
{
    private readonly List<VoiceNotice> _pending = [];
    internal int Count => _pending.Count;
    internal IReadOnlyList<VoiceNotice> Pending => _pending;

    internal void Add(VoiceNotice notice)
    {
        var duplicate = _pending.FindIndex(item => item.Key == notice.Key);
        if (duplicate >= 0)
        {
            _pending[duplicate] = notice;
            var previous = -1;
            for (var i = duplicate - 1; i >= 0; i--) if (_pending[i].Group == notice.Group) { previous = i; break; }
            if (previous >= 0) { _pending.RemoveAt(duplicate); _pending.Insert(previous, notice); }
            return;
        }
        if (notice.Family.Length > 0)
            _pending.RemoveAll(item => item.Scope == notice.Scope &&
                (item.Family == notice.Family && item.Group > 1 || notice.Terminal && item.Group == 3));
        _pending.Add(notice);
        if (_pending.Count > 512)
        {
            var lowest = _pending.Max(item => item.Group);
            _pending.RemoveAt(_pending.FindIndex(item => item.Group == lowest));
        }
    }

    internal VoiceNotice? Take(Func<VoiceNotice, bool> allowed)
    {
        var selected = _pending.Where(allowed).OrderBy(item => item.Group).FirstOrDefault();
        if (selected is not null) _pending.Remove(selected);
        return selected;
    }
    internal void RemoveWhere(Predicate<VoiceNotice> predicate) => _pending.RemoveAll(predicate);
    internal void Clear() => _pending.Clear();
}
