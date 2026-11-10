using System.IO;
using System.Windows.Threading;
using AIHub.Models;

namespace AIHub.Services.VoiceNotifications;

/// <summary>One UI actor owns ordering and playback. Package I/O never runs on the dispatcher.</summary>
internal sealed class VoiceNotificationService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly VoiceNotificationStore _store;
    private readonly IVoiceNotificationAudio _audio;
    private readonly VoiceNotificationQueue _queue = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, string> _latest = [];
    private readonly HashSet<string> _spokenTerminalEvents = [];
    private CancellationTokenSource? _currentCancellation, _gap;
    private IDisposable? _speechPause;
    private VoiceNotice? _current;
    private VoiceNotificationCatalog? _catalog;
    private VoiceNotificationPackage? _package;
    private Task? _pump;
    private int _languageVersion;
    private bool _disposed;
    internal VoiceNotificationSettings Settings { get; }
    internal string Language { get; private set; } = "ru";
    internal string? PackageError { get; private set; }
    internal int PendingCount => _queue.Count;
    internal bool Ready => _catalog is not null;
    internal bool KnowsKey(string key) => _catalog?.Find(key) is not null;
    internal event Action? Changed;
    internal VoiceNotificationService(VoiceNotificationSettings settings, VoiceNotificationStore store, Dispatcher dispatcher, IVoiceNotificationAudio? audio = null)
    {
        Settings = settings; _store = store; _dispatcher = dispatcher; _audio = audio ?? new VoiceNotificationAudio(store.CacheRoot);
        VoicePlaybackHub.Changed += PlaybackChanged;
    }
    internal async Task SetLanguageAsync(string language)
    {
        var version = ++_languageVersion; Language = language; _catalog = null; _package = null; PackageError = null; Clear();
        try
        {
            var loaded = await Task.Run(async () => {
                using var suppress = VoiceNotificationEvents.Suppress();
                var catalog = new VoiceNotificationCatalog(_store.Catalog(), language);
                catalog.AddTranslation(_store.Translation(language));
                return (catalog, package: await _store.LoadAsync(language, _lifetime.Token));
            }, _lifetime.Token);
            if (_disposed || version != _languageVersion) return;
            _catalog = loaded.catalog; _package = loaded.package;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        { if (!_disposed && version == _languageVersion) { PackageError = "VoiceNotifications.PackageUnavailable"; OwnedProcessRegistry.Log("voice_package_failed", "VoiceNotifications", detail: error.GetType().Name); } }
        if (!_disposed) Changed?.Invoke();
    }
    internal bool CanSpeak(string key, string[] contexts) => !_disposed && _catalog?.Match(key) is { } binding &&
        Settings.Allows(binding.Group) && VoicePlaybackHub.Allows(contexts) && HasClip(binding.ClipId);
    private bool HasClip(string id) => _package?.Items.TryGetValue(id, out var item) == true &&
        (_catalog?.Text(id, Language) is not { } expected || item.SourceText == expected);
    internal bool Publish(VoiceEventRequest request)
    {
        if (_disposed || _catalog?.Match(request.Value) is not { } binding || !HasClip(binding.ClipId)) return false;
        var notice = new VoiceNotice(binding.Id, binding.ClipId, request.Group ?? binding.Group, request.Scope,
            request.Contexts, request.Family, request.Terminal, request.Occurrence);
        if (notice.EventId == "localization:Tray.ResultReady" && ApplicationBackgroundOperations.Current?.State is { Phase: BackgroundOperationPhase.Completed } completed &&
            notice.Scope == "application" && notice.Contexts.Length == 0)
            notice = notice with { Scope = completed.Id, Contexts = VoiceNotificationContext.Project(completed.Project), Family = "operation", Terminal = true };
        if (!Allowed(notice)) return false;
        if (notice.Terminal && _spokenTerminalEvents.Contains(notice.Key)) return true;
        if (_current?.Key == notice.Key) return true;
        if (notice.Family.Length > 0) _latest[notice.Scope + "\n" + notice.Family] = notice.Key;
        _queue.Add(notice);
        if (_current is { } current && notice.Group < current.Group) _currentCancellation?.Cancel();
        if (notice.Group == 1) _gap?.Cancel();
        Changed?.Invoke(); StartPump(); return true;
    }
    private bool Allowed(VoiceNotice notice) => Settings.Allows(notice.Group) && VoicePlaybackHub.Allows(notice.Contexts);
    private bool CurrentState(VoiceNotice notice) => notice.Group == 1 || notice.Family.Length == 0 ||
        !_latest.TryGetValue(notice.Scope + "\n" + notice.Family, out var latest) || latest == notice.Key;
    internal void Configure()
    {
        _audio.Configure(Settings.Volume, Settings.Speed);
        _queue.RemoveWhere(item => !Allowed(item) || !CurrentState(item));
        if (_current is { } current && !Allowed(current)) _currentCancellation?.Cancel();
        if (!Settings.Enabled) Clear();
        Changed?.Invoke(); StartPump();
    }
    private void PlaybackChanged()
    {
        if (_dispatcher.CheckAccess()) { if (!_disposed) Configure(); }
        else if (!_dispatcher.HasShutdownStarted) _dispatcher.BeginInvoke(() => { if (!_disposed) Configure(); });
    }
    private void StartPump()
    {
        if (_disposed || _pump is { IsCompleted: false }) return;
        _pump = PumpAsync();
    }
    private async Task PumpAsync()
    {
        // The first yield assigns _pump before the actor can receive player callbacks.
        await Task.Yield();
        while (!_disposed && Settings.Enabled)
        {
            _queue.RemoveWhere(item => !Allowed(item) || !CurrentState(item));
            var next = _queue.Take(item => item.Group == 1 || !VoicePlaybackHub.SpeechActive);
            if (next is null) break;
            _current = next; using var attempt = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _currentCancellation = attempt;
            var interrupted = false;
            Changed?.Invoke();
            try
            {
                if (next.Group == 1) _speechPause ??= VoicePlaybackHub.SuspendSpeech();
                using var suppress = VoiceNotificationEvents.Suppress();
                if (_package?.Items.TryGetValue(next.ClipId, out var item) == true)
                {
                    OwnedProcessRegistry.Log("voice_notice_started", "VoiceNotifications", detail: next.EventId + "; group=" + next.Group + "; language=" + Language);
                    await _audio.PlayAsync(_package, item, Settings.Volume, Settings.Speed, attempt.Token);
                    if (next.Terminal) { if (_spokenTerminalEvents.Count >= 4096) _spokenTerminalEvents.Remove(_spokenTerminalEvents.First()); _spokenTerminalEvents.Add(next.Key); }
                    OwnedProcessRegistry.Log("voice_notice_ended", "VoiceNotifications", detail: next.EventId);
                }
            }
            catch (OperationCanceledException) when (attempt.IsCancellationRequested)
            { interrupted = true; if (!_disposed && Allowed(next) && CurrentState(next) && _catalog is not null) _queue.Add(next); }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
            { OwnedProcessRegistry.Log("voice_notice_failed", "VoiceNotifications", detail: error.GetType().Name); }
            finally { _current = null; _currentCancellation = null; Changed?.Invoke(); }
            if (!_queue.Pending.Any(item => item.Group == 1 && Allowed(item))) { _speechPause?.Dispose(); _speechPause = null; }
            if (_disposed || _queue.Count == 0 || !Settings.Enabled) break;
            if (interrupted && _queue.Pending.Any(item => item.Group < next.Group && Allowed(item))) continue;
            using var gap = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _gap = gap;
            var silence = Math.Max(0, 500 - _audio.LeadIn.TotalMilliseconds / Settings.Speed);
            try { await Task.Delay(TimeSpan.FromMilliseconds(silence), gap.Token); } catch (OperationCanceledException) when (gap.IsCancellationRequested) { }
            finally { _gap = null; }
        }
        _speechPause?.Dispose(); _speechPause = null;
        _pump = null;
        if (!_disposed && Settings.Enabled && _queue.Pending.Any(item => Allowed(item) && (item.Group == 1 || !VoicePlaybackHub.SpeechActive))) StartPump();
    }
    internal void Clear()
    { _queue.Clear(); _latest.Clear(); _currentCancellation?.Cancel(); _gap?.Cancel(); Changed?.Invoke(); }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; VoicePlaybackHub.Changed -= PlaybackChanged;
        _lifetime.Cancel(); Clear(); _speechPause?.Dispose(); _speechPause = null; _audio.Dispose();
    }
}
