namespace AIHub.Models;

public sealed record PublisherRelease(long Id, string Tag, string Title, string Body,
    DateTimeOffset PublishedAt, string Url);

public sealed class PublisherState
{
    public int SchemaVersion { get; set; } = 1;
    public List<PublisherDirection> Directions { get; set; } = [];
}

public sealed class PublisherDirection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Repository { get; set; } = "";
    public long CommunityId { get; set; }
    public string CommunityName { get; set; } = "";
    public string ProtectedToken { get; set; } = "";
    public bool AutoPublish { get; set; }
    public int CheckMinutes { get; set; } = 60;
    public DateTimeOffset? LastCheck { get; set; }
    public DateTimeOffset? LastSourceChange { get; set; }
    public DateTimeOffset? NextCheck { get; set; }
    public string StatusKey { get; set; } = "Publisher.Ready";
    public List<PublisherRelease> Releases { get; set; } = [];
    public List<long> SeenReleaseIds { get; set; } = [];
    public List<long> InitialReleaseIds { get; set; } = [];
    public List<PublisherPublication> Publications { get; set; } = [];
    public List<long> Queue { get; set; } = [];
    public List<long> AutomaticReleaseIds { get; set; } = [];
    public int QueueHours { get; set; }
    public bool QueuePaused { get; set; }
    public DateTimeOffset? NextPublication { get; set; }
    public List<PublisherEvent> Events { get; set; } = [];
}

public sealed class PublisherPublication
{
    public long ReleaseId { get; set; }
    public string Guid { get; set; } = System.Guid.NewGuid().ToString("N")[..16];
    public string Status { get; set; } = "sending";
    public long? PostId { get; set; }
    public DateTimeOffset AttemptedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PublishedAt { get; set; }
}

public sealed record PublisherEvent(DateTimeOffset Time, string Key, long? ReleaseId = null);

public sealed class PublisherException(string key, int? apiCode = null) : Exception(key)
{
    public string Key { get; } = key;
    public int? ApiCode { get; } = apiCode;
}
