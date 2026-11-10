namespace AIHub.Services.VoiceNotifications;

/// <summary>Only our players register here. No Windows-wide media hooks or microphone access.</summary>
internal static class VoicePlaybackHub
{
    private static readonly object Gate = new();
    private static readonly HashSet<SpeechLease> Speech = [];
    private static readonly Dictionary<object, string[]> Media = [];
    private static readonly HashSet<object> Microphones = [];
    private static int _holds;
    private static readonly List<Action> Held = [];
    internal static event Action? Changed;
    internal static bool SpeechActive { get { lock (Gate) return Speech.Any(item => item.Active); } }
    internal static bool Recording { get { lock (Gate) return Microphones.Count > 0; } }
    internal static bool NotificationsInterrupting { get { lock (Gate) return _holds > 0; } }
    internal static bool Allows(string[] contexts)
    {
        lock (Gate) return Microphones.Count == 0 && (Media.Count == 0 ||
            Media.Values.Any(values => values.Intersect(contexts, StringComparer.OrdinalIgnoreCase).Any()));
    }
    internal static void MediaState(object owner, bool playing, params string[] contexts)
    {
        var changed = false;
        lock (Gate)
        {
            if (playing) { changed = !Media.TryGetValue(owner, out var old) || !old.SequenceEqual(contexts); Media[owner] = contexts; }
            else changed = Media.Remove(owner);
        }
        if (changed) Signal();
    }
    internal static void MicrophoneState(object owner, bool recording)
    { bool changed; lock (Gate) changed = recording ? Microphones.Add(owner) : Microphones.Remove(owner); if (changed) Signal(); }
    private static void Signal()
    {
        foreach (Action listener in Changed?.GetInvocationList() ?? [])
            try { listener(); } catch (Exception error) { OwnedProcessRegistry.Log("voice_playback_observer_failed", "VoiceNotifications", detail: error.GetType().Name); }
    }
    internal static SpeechLease RegisterSpeech(Action pause, Action resume)
    {
        var lease = new SpeechLease(pause, resume);
        lock (Gate)
        {
            Speech.Add(lease);
            if (_holds > 0 && lease.Suspend() is { } release) Held.Add(release);
        }
        Signal(); return lease;
    }
    internal static IDisposable SuspendSpeech()
    {
        lock (Gate)
        {
            if (++_holds == 1)
                try { foreach (var item in Speech.Where(item => item.Active)) if (item.Suspend() is { } release) Held.Add(release); }
                catch { ReleaseHold(); throw; }
        }
        return new Release(ReleaseHold);
    }
    private static void ReleaseHold()
    {
        Action[] actions;
        lock (Gate) { if (_holds == 0 || --_holds > 0) return; actions = Held.ToArray(); Held.Clear(); }
        foreach (var action in actions) action(); Signal();
    }
    private sealed class Release(Action action) : IDisposable
    { private Action? _action = action; public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke(); }

    internal sealed class SpeechLease(Action pause, Action resume) : IDisposable
    {
        private bool _disposed, _suspended;
        internal bool Active { get; private set; } = true;
        internal bool IsSuspended { get { lock (Gate) return _suspended; } }
        internal void SetActive(bool active)
        {
            lock (Gate)
            {
                if (_disposed) return; Active = active;
                if (active && _holds > 0 && Suspend() is { } release) Held.Add(release);
            }
            Signal();
        }
        internal Action? Suspend()
        {
            lock (Gate) { if (_disposed || !Active || _suspended) return null; _suspended = true; }
            try { pause(); }
            catch { lock (Gate) _suspended = false; throw; }
            return () => {
                bool shouldResume; lock (Gate) { shouldResume = !_disposed && Active; _suspended = false; }
                if (shouldResume) try { resume(); } catch (Exception error) { OwnedProcessRegistry.Log("voice_resume_failed", "VoiceNotifications", detail: error.GetType().Name); }
                Signal();
            };
        }
        public void Dispose()
        { lock (Gate) { if (_disposed) return; _disposed = true; Active = false; Speech.Remove(this); } Signal(); }
    }
}
