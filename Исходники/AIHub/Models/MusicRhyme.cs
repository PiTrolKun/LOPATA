using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHub.Models;

public sealed record MusicRhymeReading(string Word, string Language, string Key, int Syllables, string Display, bool Uncertain = false);
public sealed record MusicRhymeLine(int Number, int Start, int Length, int Stanza, string Text, string Ending,
    int EndingStart, string Language, int? Syllables, MusicRhymeReading[] Readings, bool Performance = false);
public sealed record MusicRhymeIssue(int Line, string Kind, int? Partner = null);
public sealed record MusicRhymeFragment(int[] Lines, string Kind, double Score, string ReasonKey, string[] Arguments)
{
    public int[] Context { get; init; } = [];
}
public sealed record MusicRhymeAnalysis(MusicRhymeLine[] Lines, MusicRhymeIssue[] Issues)
{
    // Version zero is a saved analysis created before editorial fragment selection existed.
    public int SelectionVersion { get; init; }
    public MusicRhymeFragment[] Fragments { get; init; } = [];
    public int[][] PreserveRhymePairs { get; init; } = [];
}
public sealed record MusicRhymeSuggestion(int Line, string Word, string Reading, string Source, bool Synonym)
{
    public double SoundScore { get; init; }
    public double MeaningScore { get; init; }
    public double GrammarScore { get; init; }
    public bool GrammarKnown { get; init; }
    public string Relation { get; init; } = "unknown";
    public double Score { get; init; }
}
public sealed record MusicRhymeProgramChange(int Line, string Before, string After, string Reason,
    double SoundBefore = 0, double SoundAfter = 0, string Relation = "", bool GrammarKnown = false);

/// <summary>Immutable computational input; later editing of any visible column cannot change it.</summary>
public sealed record MusicRhymeRequest(string Id, string ProjectId, string Source, int Start, int Length,
    string ModelId, string? ModelPath, string ModelsRoot, string InterfaceLanguage)
{
    public AIHub.Services.MusicRhymeTuning Tuning { get; init; } = new();
    public bool CreativeMode { get; init; }
    public bool UsePersonalLyricsLibrary { get; init; }
    [JsonIgnore] public string Text => Source.Substring(Start, Length);
    public void Validate()
    {
        if (Tuning is null) throw new System.IO.InvalidDataException("Music.Rhyme.Settings.Invalid");
        Tuning.Validate();
        if (!Guid.TryParseExact(Id, "N", out _) || !Guid.TryParseExact(ProjectId, "N", out _) || Source is null
            || Source.Length > Tuning.Integer("base.source_limit")
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
    public MusicRhymeProgramChange[] ProgramChanges { get; init; } = [];
    public string Original { get; init; } = "";
    public string Program { get; init; } = "";
    public string Model { get; init; } = "";
    public string ProgramDraft { get; init; } = "";
    public string Reply { get; init; } = "";
    public string Prompt { get; init; } = "";
    public string Reference { get; init; } = "";
    public AIHub.Services.MusicLibrary.LyricExample[] LibraryExamples { get; init; } = [];
    public bool LibraryExamplesReady { get; init; }
    public string CreativeSummary { get; init; } = "";
    public string CreativeSummaryPrompt { get; init; } = "";
    public string? Error { get; init; }
    public bool ProgramReady { get; init; }
    public bool AutomaticPreview { get; init; }
    public bool GenerationRequested { get; init; }
    public bool ModelReady { get; init; }
    public bool ModelSkipped { get; init; }
    public bool OriginalEdited { get; init; }
    public bool ProgramEdited { get; init; }
    public bool ModelEdited { get; init; }
    public MusicRhymeStep Snapshot() => JsonSerializer.Deserialize<MusicRhymeStep>(JsonSerializer.Serialize(this))!;
    public void Validate()
    {
        Request.Validate();
        if (LibraryExamples is null || LibraryExamples.Length > 8 || LibraryExamples.Any(e => e is null || e.Text is null || e.Text.Length > 16000)
            || Original is null || Program is null || Model is null || ProgramDraft is null || Reply is null || Prompt is null || Reference is null
            || CreativeSummary is null || CreativeSummaryPrompt is null
            || new[] { Original, Program, Model, ProgramDraft, Reply, Prompt, Reference, CreativeSummary, CreativeSummaryPrompt }.Any(s => s.Length > 400_000)
            || Suggestions is null || Suggestions.Length > 1000 || ProgramChanges is null || ProgramChanges.Length > 32
            || Stage is not ("Analysis" or "Candidates" or "Summary" or "Reference" or "Model" or "Ready" or "Partial"))
            throw new System.IO.InvalidDataException("Music.Rhyme.InvalidInput");
    }
}
