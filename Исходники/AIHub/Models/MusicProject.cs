using AIHub.Services;

namespace AIHub.Models;

public enum MusicHistoryMode { Text, Settings, Off }
public enum MusicProjectOutcome { Pending, Running, Paused, Completed, Cancelled, Failed, Interrupted }

public sealed record MusicProjectSnapshot
{
    public string Lyrics { get; init; } = "";
    public string Model { get; init; } = MusicExpertCatalog.Model;
    public string Variation { get; init; } = MusicComponentCatalog.ModelId;
    public string ModelRevision { get; init; } = MusicComponentCatalog.Revision;
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Comment { get; init; } = "";
    public int Variants { get; init; } = 1;
    public int? DurationSeconds { get; init; }
    public string OutputFolder { get; init; } = "";
    public MusicExpertSettings Expert { get; init; } = new();
    public Dictionary<string, MusicExpertSettings> ModelSettings { get; init; } = new();
    public MusicOutputSettings Output { get; init; } = new();
    public MusicWishSnapshot Wishes { get; init; } = MusicWishSnapshot.Capture(new());
    public MusicProjectSnapshot Snapshot() => this with { Expert = Expert.Snapshot(), Wishes = Wishes.Snapshot(),
        ModelSettings = ModelSettings.ToDictionary(p => p.Key, p => p.Value.Snapshot()) };
    public void Validate()
    {
        if (Lyrics is null || Title is null || Artist is null || Comment is null || string.IsNullOrWhiteSpace(Model)
            || string.IsNullOrWhiteSpace(Variation) || string.IsNullOrWhiteSpace(ModelRevision)
            || OutputFolder is null || Variants is < 1 or > 8 || DurationSeconds is { } seconds && seconds is < 1 or > 360
            || Expert is null || Output is null || Wishes is null || Wishes.Selections is null || Wishes.Performers is null)
            throw new System.IO.InvalidDataException("Invalid music project snapshot.");
        Expert.Validate(); Output.Validate();
        if (Expert.Variation != Variation || ModelSettings is null || ModelSettings.Count > 100)
            throw new System.IO.InvalidDataException("Invalid project model settings.");
        foreach (var pair in ModelSettings) {
            if (pair.Value is null || pair.Key != pair.Value.Variation) throw new System.IO.InvalidDataException("Invalid project settings bank.");
            pair.Value.Validate();
        }
        if (Wishes.Selections.Any(p => p.Value is null || p.Value.Any(v => v is null))
            || Wishes.Performers.Any(p => p is null || p.Name is null || p.Id is null || p.Timbres is null || p.Delivery is null))
            throw new System.IO.InvalidDataException("Invalid music project wishes.");
    }
    public static MusicProjectSnapshot FromJob(MusicGenerationJob job) => new() {
        Model = MusicAceCatalog.IsAce(job.Variation) ? MusicAceCatalog.ModelName : MusicExpertCatalog.Model,
        Lyrics = job.Lyrics, Title = job.Title, Artist = job.Artist, Comment = job.Comment, ModelRevision = job.ModelRevision, Variation = job.Variation,
        Variants = job.Variants.Length, DurationSeconds = job.DurationAutomatic ? null : job.DurationSeconds, OutputFolder = job.OutputFolder,
        Expert = job.Expert.Snapshot(), Output = job.Output ?? new() { Format = MusicAudioFormat.Wav },
        Wishes = job.Wishes?.Snapshot() ?? MusicWishSnapshot.Capture(new()) };
}

public sealed record MusicProjectStep(int Number, string JobId, DateTimeOffset CreatedAt, MusicProjectSnapshot Snapshot)
{
    public MusicProjectOutcome Outcome { get; init; }
    public string Message { get; init; } = "";
}

public sealed record MusicProject(string Id, long Number, DateTimeOffset CreatedAt)
{
    public int Schema { get; init; } = 1;
    public string Name { get; init; } = "";
    public bool Manual { get; init; }
    public bool Persistent { get; init; }
    public MusicProjectSnapshot Saved { get; init; } = new();
    public MusicProjectStep[] Steps { get; init; } = [];
}
