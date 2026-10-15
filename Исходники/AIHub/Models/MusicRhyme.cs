using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHub.Models;

public sealed record MusicRhymeReading(string Word, string Language, string Key, int Syllables, string Display, bool Uncertain = false);
public sealed record MusicRhymeLine(int Number, int Start, int Length, int Stanza, string Text, string Ending,
    int EndingStart, string Language, int? Syllables, MusicRhymeReading[] Readings, bool Performance = false);
public sealed record MusicRhymeIssue(int Line, string Kind, int? Partner = null);
public sealed record MusicRhymeAnalysis(MusicRhymeLine[] Lines, MusicRhymeIssue[] Issues);
public sealed record MusicRhymeSuggestion(int Line, string Word, string Reading, string Source, bool Synonym);

/// <summary>Immutable computational input; later editing of any visible column cannot change it.</summary>
public sealed record MusicRhymeRequest(string Id, string ProjectId, string Source, int Start, int Length,
    string ModelId, string? ModelPath, string ModelsRoot, string InterfaceLanguage)
{
    [JsonIgnore] public string Text => Source.Substring(Start, Length);
    public void Validate()
    {
        if (!Guid.TryParseExact(Id, "N", out _) || !Guid.TryParseExact(ProjectId, "N", out _) || Source is null || Source.Length > 200_000
            || Start < 0 || Length <= 0 || Start > Source.Length - Length || ModelId == "profile-hydra")
            throw new System.IO.InvalidDataException("Music.Rhyme.InvalidInput");
    }
}

public sealed record MusicRhymeStep
{
    public required MusicRhymeRequest Request { get; init; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
    public string Stage { get; init; } = "Analysis";
    public MusicRhymeAnalysis? Analysis { get; init; }
    public MusicRhymeSuggestion[] Suggestions { get; init; } = [];
    public string Original { get; init; } = "";
    public string Program { get; init; } = "";
    public string Model { get; init; } = "";
    public string ProgramDraft { get; init; } = "";
    public string Reply { get; init; } = "";
    public string Prompt { get; init; } = "";
    public string Reference { get; init; } = "";
    public string? Error { get; init; }
    public bool ProgramReady { get; init; }
    public bool ModelReady { get; init; }
    public bool ModelSkipped { get; init; }
    public bool OriginalEdited { get; init; }
    public bool ProgramEdited { get; init; }
    public bool ModelEdited { get; init; }
    public MusicRhymeStep Snapshot() => JsonSerializer.Deserialize<MusicRhymeStep>(JsonSerializer.Serialize(this))!;
    public void Validate()
    {
        Request.Validate();
        if (Original is null || Program is null || Model is null || ProgramDraft is null || Reply is null || Prompt is null || Reference is null
            || new[] { Original, Program, Model, ProgramDraft, Reply, Prompt, Reference }.Any(s => s.Length > 400_000)
            || Suggestions is null || Suggestions.Length > 1000 || Stage is not ("Analysis" or "Candidates" or "Reference" or "Model" or "Ready" or "Partial"))
            throw new System.IO.InvalidDataException("Music.Rhyme.InvalidInput");
    }
}
