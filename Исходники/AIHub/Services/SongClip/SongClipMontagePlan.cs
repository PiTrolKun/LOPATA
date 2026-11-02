using System.IO;
using System.Text.Json.Serialization;

namespace AIHub.Services.SongClip;

internal sealed record SongClipMontageSlot(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("startSeconds")] double StartSeconds,
    [property: JsonPropertyName("endSeconds")] double EndSeconds)
{
    [JsonIgnore] public double DurationSeconds => EndSeconds - StartSeconds;
}

/// <summary>CPU-only upstream beat analysis. Its immutable slots, not a language model, own timing.</summary>
internal sealed record SongClipMontageAnalysis
{
    [JsonPropertyName("version")] public int Version { get; init; } = 1;
    [JsonPropertyName("durationSeconds")] public double DurationSeconds { get; init; }
    [JsonPropertyName("fps")] public int Fps { get; init; } = 24;
    [JsonPropertyName("beatTimes")] public double[] BeatTimes { get; init; } = [];
    [JsonPropertyName("slots")] public SongClipMontageSlot[] Slots { get; init; } = [];
    [JsonPropertyName("analyzerRevision")] public string AnalyzerRevision { get; init; } = "";

    internal void Validate()
    {
        if (Version != 1 || Fps != 24 || !double.IsFinite(DurationSeconds) || DurationSeconds is <= 0 or > 7_200
            || AnalyzerRevision != SongClipMontageProtocol.UpstreamRevision || BeatTimes is null || BeatTimes.Length > 100_000
            || Slots is null || Slots.Length is < 1 or > SongClipMontageProtocol.MaximumShots)
            throw new InvalidDataException(SongClipMontageProtocol.Mismatch);
        double previousBeat = -1, end = 0;
        foreach (var beat in BeatTimes) {
            if (!double.IsFinite(beat) || beat < 0 || beat > DurationSeconds || beat <= previousBeat)
                throw new InvalidDataException(SongClipMontageProtocol.Mismatch);
            previousBeat = beat;
        }
        foreach (var (slot, offset) in Slots.Select((slot, offset) => (slot, offset))) {
            if (slot is null || slot.Index != offset + 1 || !double.IsFinite(slot.StartSeconds) || !double.IsFinite(slot.EndSeconds)
                || Math.Abs(slot.StartSeconds - end) > .000001 || slot.DurationSeconds <= 0 || slot.DurationSeconds > 15.5
                || slot.EndSeconds > DurationSeconds + .000001)
                throw new InvalidDataException(SongClipMontageProtocol.Mismatch);
            end = slot.EndSeconds;
        }
        if (Math.Abs(end - DurationSeconds) > .000001) throw new InvalidDataException(SongClipMontageProtocol.Mismatch);
    }
}

internal sealed record SongClipMontageShot(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("startSeconds")] double StartSeconds,
    [property: JsonPropertyName("durationSeconds")] double DurationSeconds,
    [property: JsonPropertyName("prompt")] string Prompt,
    [property: JsonPropertyName("lyricAnchors")] string[] LyricAnchors,
    [property: JsonPropertyName("transition")] string Transition,
    [property: JsonPropertyName("performerVisible")] bool PerformerVisible,
    [property: JsonPropertyName("referenceIndices")] int[] ReferenceIndices,
    [property: JsonPropertyName("shotSize")] string ShotSize,
    [property: JsonPropertyName("cameraAngle")] string CameraAngle);

/// <summary>Host-validated director data consumed by the pinned Any Song router/composer.</summary>
internal sealed record SongClipMontagePlan
{
    [JsonPropertyName("version")] public int Version { get; init; } = 1;
    [JsonPropertyName("framesPerSecond")] public int FramesPerSecond { get; init; } = 24;
    [JsonPropertyName("inputHash")] public string InputHash { get; init; } = "";
    [JsonPropertyName("globalStyle")] public string GlobalStyle { get; init; } = "";
    [JsonPropertyName("shots")] public SongClipMontageShot[] Shots { get; init; } = [];
    [JsonPropertyName("assistants"), JsonConverter(typeof(SongClipAssistantHistoryJsonConverter))]
    public OutputModelUse[] Assistants { get; init; } = [];
    [JsonPropertyName("referenceRoles"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SongClipReferenceRole[]? ReferenceRoles { get; init; }
}
