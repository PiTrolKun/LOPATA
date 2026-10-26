using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services.MusicScore;

namespace AIHub.Services.VideoEditor;

/// <summary>The authors' Vocal-to-Ins workflow, using our pinned CPU-only upstream helper.</summary>
internal sealed class VideoInstrumentalWorker(IMusicYueWorker inner) : IMusicYueWorker
{
    public event Action<string>? Log { add => inner.Log += value; remove => inner.Log -= value; }
    public string? LastHardware => inner.LastHardware;
    public string? LastRuntimePack => inner.LastRuntimePack;
    public string? LastRequestReceipt => inner.LastRequestReceipt;
    public async Task PlanAsync(string model, MusicYueRequest request, string requestPath, string planPath, CancellationToken token)
    {
        await inner.PlanAsync(model, request, requestPath, planPath, token);
        var raw = await File.ReadAllTextAsync(planPath, token);
        await File.WriteAllTextAsync(planPath + ".model.abc", raw, token);
        var converted = await ConvertAsync(raw, requestPath, token);
        var temporary = planPath + ".instrumental.tmp";
        try { await File.WriteAllTextAsync(temporary, converted.Abc, token); token.ThrowIfCancellationRequested(); File.Move(temporary, planPath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async Task SynthesizeAsync(string model, string decoder, MusicYueRequest request, string requestPath, string outputPath, CancellationToken token)
    {
        if (request.EffectiveExpert.Cot == "off" || !string.IsNullOrWhiteSpace(request.Lyrics)) throw new InvalidDataException("VideoEditor.MusicInvalid");
        // Also fixes the input of an unfinished legacy video-music job without altering its saved plan/hash.
        var converted = await ConvertAsync(request.Abc, requestPath, token);
        await inner.SynthesizeAsync(model, decoder, request with { Abc = converted.Abc }, requestPath, outputPath, token);
    }
    private static async Task<MusicScoreInspection> ConvertAsync(string raw, string requestPath, CancellationToken token)
    {
        var before = await MusicScoreNative.InspectAsync(raw, token);
        var after = await MusicScoreNative.InstrumentalAsync(raw, token);
        if (after.Sheet.Notes.Vocal.Length != 0 || after.Sheet.Notes.Ins.Length == 0 || before.Sheet.Bpm != after.Sheet.Bpm || before.Sheet.Total != after.Sheet.Total
            || !before.Sheet.Chords.SequenceEqual(after.Sheet.Chords) || before.Sheet.Notes.Vocal.Any(note => !after.Sheet.Notes.Ins.Contains(note))) throw new InvalidDataException("VideoEditor.MusicInvalid");
        await File.WriteAllTextAsync(requestPath + ".instrumental.json", JsonSerializer.Serialize(new {
            SourceHash = MusicScoreVersion.Stamp(raw), ResultHash = MusicScoreVersion.Stamp(after.Abc),
            Moved = before.Sheet.Notes.Vocal.Length, VocalNotes = after.Sheet.Notes.Vocal.Length, InstrumentNotes = after.Sheet.Notes.Ins.Length,
            Parser = MusicScoreNative.HelperHash, Method = "YuE2 upstream Vocal-to-Ins event transfer" }), token);
        return after;
    }
}
