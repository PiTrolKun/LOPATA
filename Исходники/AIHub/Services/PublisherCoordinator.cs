using AIHub.Models;

namespace AIHub.Services;

/// <summary>Independent lightweight networking, with durable write-before-send bookkeeping.</summary>
public sealed class PublisherCoordinator : IAsyncDisposable
{
    private readonly PublisherStore _store;
    private readonly IPublisherGitHubClient _github;
    private readonly IPublisherVkClient _vk;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private PublisherState _state;
    private Task? _loop;
    public string? FailureKey { get; private set; }
    public event Action? Changed;
    public PublisherCoordinator(PublisherStore store, IPublisherGitHubClient github, IPublisherVkClient vk)
    { _store = store; _github = github; _vk = vk; _state = store.Load(); _store.Save(_state); }
    public PublisherState Snapshot() { lock (_stateLock) return PublisherStore.Copy(_state); }
    public void Start() => _loop ??= Task.Run(LoopAsync);

    private void Commit(PublisherState state)
    {
        _store.Save(state);
        lock (_stateLock) _state = PublisherStore.Copy(state);
        FailureKey = null;
        Changed?.Invoke();
    }
    private async Task RunAsync(Func<PublisherState, CancellationToken, Task> action, CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        await _gate.WaitAsync(linked.Token);
        try { var state = Snapshot(); await action(state, linked.Token); Commit(state); }
        finally { _gate.Release(); }
    }
    private static PublisherDirection Find(PublisherState state, Guid id) =>
        state.Directions.FirstOrDefault(d => d.Id == id) ?? throw new PublisherException("Publisher.DirectionMissing");
    private static void Record(PublisherDirection direction, string key, long? release = null)
    {
        direction.StatusKey = key; direction.Events.Add(new(DateTimeOffset.UtcNow, key, release));
        if (direction.Events.Count > 500) direction.Events.RemoveRange(0, direction.Events.Count - 500);
    }

    public Task AddAsync(PublisherDirection direction, string secret, CancellationToken token = default) => RunAsync(async (state, ct) =>
    {
        if (state.Directions.Any(d => d.Repository.Equals(direction.Repository, StringComparison.OrdinalIgnoreCase)
            && d.CommunityId == direction.CommunityId)) throw new PublisherException("Publisher.DuplicateDirection");
        direction.ProtectedToken = PublisherSecretProtection.Protect(secret);
        direction.Releases = await _github.ReadAsync(direction.Repository, ct);
        direction.SeenReleaseIds = direction.Releases.Select(r => r.Id).ToList();
        direction.InitialReleaseIds = direction.SeenReleaseIds.ToList();
        direction.LastSourceChange = direction.Releases.LastOrDefault()?.PublishedAt;
        direction.LastCheck = DateTimeOffset.UtcNow; direction.NextCheck = direction.LastCheck.Value.AddMinutes(direction.CheckMinutes);
        Record(direction, "Publisher.Ready"); state.Directions.Add(direction);
    }, token);

    public Task DeleteAsync(Guid id) => RunAsync((state, _) =>
    { state.Directions.Remove(Find(state, id)); return Task.CompletedTask; });
    public Task ReplaceTokenAsync(Guid id, string secret) => RunAsync(async (state, ct) =>
    {
        var direction = Find(state, id);
        await _vk.CheckAsync(direction.CommunityId.ToString(System.Globalization.CultureInfo.InvariantCulture), secret, ct);
        direction.ProtectedToken = PublisherSecretProtection.Protect(secret); Record(direction, "Publisher.AccessChecked");
    });
    public Task ConfigureAsync(Guid id, bool autoPublish, int checkMinutes) => RunAsync((state, _) =>
    {
        var d = Find(state, id); d.AutoPublish = autoPublish; d.CheckMinutes = Math.Clamp(checkMinutes, 15, 1440);
        if (!autoPublish)
        {
            d.Queue.RemoveAll(release => d.AutomaticReleaseIds.Contains(release)); d.AutomaticReleaseIds.Clear();
            if (d.Queue.Count == 0) { d.NextPublication = null; d.QueueHours = 0; }
        }
        d.NextCheck = DateTimeOffset.UtcNow; return Task.CompletedTask;
    });
    public Task CheckAsync(Guid id) => RunAsync((state, ct) => CheckCoreAsync(Find(state, id), ct));

    private async Task CheckCoreAsync(PublisherDirection d, CancellationToken token)
    {
        var releases = await _github.ReadAsync(d.Repository, token);
        var newItems = releases.Where(r => !d.SeenReleaseIds.Contains(r.Id)).ToArray();
        if (d.AutoPublish)
            foreach (var item in newItems)
                if (!d.Queue.Contains(item.Id) && !d.Publications.Any(p => p.ReleaseId == item.Id && p.Status is "published" or "sending" or "uncertain"))
                { d.Queue.Add(item.Id); d.AutomaticReleaseIds.Add(item.Id); }
        foreach (var item in newItems) d.SeenReleaseIds.Add(item.Id);
        d.Releases = releases; d.LastCheck = DateTimeOffset.UtcNow;
        d.LastSourceChange = releases.LastOrDefault()?.PublishedAt;
        d.NextCheck = d.LastCheck.Value.AddMinutes(d.CheckMinutes);
        Record(d, newItems.Length > 0 ? "Publisher.NewReleases" : "Publisher.Checked");
    }

    public Task EnqueueAsync(Guid id, IEnumerable<long> releases, int hours, bool allowRepeat = false) => RunAsync((state, _) =>
    {
        var d = Find(state, id); var selected = releases.ToHashSet();
        if (selected.Count == 0 || selected.Any(r => d.Releases.All(item => item.Id != r))) throw new PublisherException("Publisher.SelectReleases");
        foreach (var item in d.Releases.Where(r => selected.Contains(r.Id)).OrderBy(r => r.PublishedAt).ThenBy(r => r.Id))
        {
            if (d.Publications.Any(p => p.ReleaseId == item.Id && p.Status is "uncertain" or "sending"))
                throw new PublisherException("Publisher.Uncertain");
            if (!allowRepeat && d.Publications.Any(p => p.ReleaseId == item.Id && p.Status == "published"))
                throw new PublisherException("Publisher.RepeatRequired");
            if (!d.Queue.Contains(item.Id)) d.Queue.Add(item.Id);
            d.AutomaticReleaseIds.Remove(item.Id);
        }
        d.Queue = d.Queue.OrderBy(id => d.Releases.FirstOrDefault(r => r.Id == id)?.PublishedAt ?? DateTimeOffset.MaxValue)
            .ThenBy(id => id).ToList();
        d.QueueHours = Math.Clamp(hours, 0, 720); d.QueuePaused = false;
        d.NextPublication ??= DateTimeOffset.UtcNow; Record(d, "Publisher.Queued"); return Task.CompletedTask;
    });
    public Task QueueControlAsync(Guid id, bool paused, bool clear = false) => RunAsync((state, _) =>
    {
        var d = Find(state, id); d.QueuePaused = paused;
        if (clear) { d.Queue.Clear(); d.AutomaticReleaseIds.Clear(); d.NextPublication = null; }
        return Task.CompletedTask;
    });

    public Task PublishAsync(Guid id, long releaseId, string shortenedLabel, bool repeat = false) => RunAsync(async (state, ct) =>
    {
        var d = Find(state, id);
        if (d.Publications.Any(p => p.ReleaseId == releaseId && p.Status is "sending" or "uncertain")) throw new PublisherException("Publisher.Uncertain");
        if (!repeat && d.Publications.Any(p => p.ReleaseId == releaseId && p.Status == "published")) throw new PublisherException("Publisher.RepeatRequired");
        await SendCoreAsync(state, d, releaseId, shortenedLabel, ct);
    });

    private async Task SendCoreAsync(PublisherState state, PublisherDirection d, long releaseId, string shortenedLabel, CancellationToken token)
    {
        var release = d.Releases.FirstOrDefault(r => r.Id == releaseId) ?? throw new PublisherException("Publisher.ReleaseMissing");
        var message = PublisherPostFormatter.Format(d.Repository, release, shortenedLabel);
        string secret;
        try { secret = PublisherSecretProtection.Unprotect(d.ProtectedToken); }
        catch (Exception error) when (error is System.Security.Cryptography.CryptographicException or FormatException)
        { throw new PublisherException("Publisher.SecretUnavailable"); }
        var attempt = new PublisherPublication { ReleaseId = releaseId };
        d.Publications.Add(attempt); Record(d, "Publisher.Sending", releaseId); Commit(state);
        try
        {
            attempt.PostId = await _vk.PostAsync(d.CommunityId, secret, message, attempt.Guid, token);
            attempt.PublishedAt = DateTimeOffset.UtcNow; attempt.Status = "published";
            d.Queue.Remove(releaseId);
            d.AutomaticReleaseIds.Remove(releaseId);
            if (d.Queue.All(id => d.AutomaticReleaseIds.Contains(id))) d.QueueHours = 0;
            d.NextPublication = d.Queue.Count > 0 ? DateTimeOffset.UtcNow.AddHours(d.QueueHours) : null;
            if (d.Queue.Count == 0) d.QueueHours = 0;
            Record(d, "Publisher.Published", releaseId);
        }
        catch (PublisherException error) when (error.Key is "Publisher.InvalidToken" or "Publisher.RateLimit" or "Publisher.VkPermission" or "Publisher.VkError")
        { attempt.Status = "failed"; d.QueuePaused = true; Record(d, error.Key, releaseId); }
        catch (Exception)
        { attempt.Status = "uncertain"; d.QueuePaused = true; Record(d, "Publisher.Uncertain", releaseId); }
        finally { secret = ""; }
        Commit(state);
    }

    // An explicit operator confirmation, not automatic reconciliation based on a guess.
    public Task ResolveAsync(Guid id, long releaseId, long? existingPostId) => RunAsync((state, _) =>
    {
        var d = Find(state, id); var attempt = d.Publications.LastOrDefault(p => p.ReleaseId == releaseId && p.Status == "uncertain")
            ?? throw new PublisherException("Publisher.Uncertain");
        if (existingPostId is > 0)
        { attempt.Status = "published"; attempt.PostId = existingPostId; attempt.PublishedAt = DateTimeOffset.UtcNow; d.Queue.Remove(releaseId); d.AutomaticReleaseIds.Remove(releaseId); }
        else attempt.Status = "failed";
        d.NextPublication = DateTimeOffset.UtcNow.AddHours(d.QueueHours);
        Record(d, existingPostId is > 0 ? "Publisher.Published" : "Publisher.Checked"); return Task.CompletedTask;
    });

    public async Task TickAsync(DateTimeOffset now, string shortenedLabel, CancellationToken token = default)
    {
        await RunAsync(async (state, ct) =>
        {
            foreach (var d in state.Directions)
            {
                if (d.NextCheck is null || d.NextCheck <= now)
                {
                    try { await CheckCoreAsync(d, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception error)
                    { Record(d, error is PublisherException known ? known.Key : "Publisher.NetworkError"); d.NextCheck = now.AddMinutes(Math.Max(60, d.CheckMinutes)); }
                    Commit(state);
                }
                if (d.QueuePaused || d.Queue.Count == 0 || d.NextPublication > now
                    || d.Publications.Any(p => p.Status is "uncertain" or "sending")) continue;
                try { await SendCoreAsync(state, d, d.Queue[0], shortenedLabel, ct); }
                catch (PublisherException error)
                { d.QueuePaused = true; Record(d, error.Key, d.Queue[0]); Commit(state); }
                // At most one scheduled post per direction per tick; no catch-up avalanche.
            }
        }, token);
    }

    public Func<string> ShortenedLabel { get; set; } = () => "Full release: see link.";
    private async Task LoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            do
            {
                try { await TickAsync(DateTimeOffset.UtcNow, ShortenedLabel(), _lifetime.Token); }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { break; }
                catch (Exception)
                { FailureKey = "Publisher.StoreUnavailable"; Changed?.Invoke(); }
            } while (await timer.WaitForNextTickAsync(_lifetime.Token));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync(); if (_loop is not null) await _loop;
        await _gate.WaitAsync(); try { _store.Dispose(); } finally { _gate.Release(); }
        _lifetime.Dispose();
    }
}
