using System.IO;
using System.Text.Json;
using AIHub.Models;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace AIHub.Services.MusicMidi;

public sealed record MusicMidiTrack(string Name, MusicMelody Melody, bool Polyphonic, int OriginalNotes);

public static class MusicMidiFile
{
    public static MusicMidiTrack[] Read(string path)
    {
        if (new FileInfo(path).Length > 8_000_000) throw new InvalidDataException("Music.Midi.Limit");
        var midi = MidiFile.Read(path); var tempo = midi.GetTempoMap();
        var tempoEvents = midi.GetTimedEvents().Where(e => e.Event is SetTempoEvent).ToArray();
        var bpm = (int)Math.Round(60_000_000d / (tempoEvents.FirstOrDefault()?.Event is SetTempoEvent t ? t.MicrosecondsPerQuarterNote : 500000));
        if (bpm is < 40 or > 240) throw new InvalidDataException("Music.Midi.TempoRange");
        var signature = midi.GetTimedEvents().Select(e => e.Event).OfType<TimeSignatureEvent>().FirstOrDefault();
        var results = new List<MusicMidiTrack>(); var trackIndex = 0;
        foreach (var track in midi.GetTrackChunks()) {
            if (++trackIndex > 256) throw new InvalidDataException("Music.Midi.Limit");
            foreach (var channel in track.GetNotes().GroupBy(n => (int)n.Channel)) {
                if (channel.Key == 9) continue;
                var original = channel.ToArray(); if (original.Length > 20000) throw new InvalidDataException("Music.Midi.Limit");
                var notes = original.Select(n => {
                    var start = TimeConverter.ConvertTo<MetricTimeSpan>(n.Time, tempo).TotalMicroseconds / 1_000_000d;
                    var end = TimeConverter.ConvertTo<MetricTimeSpan>(n.EndTime, tempo).TotalMicroseconds / 1_000_000d;
                    int Tick(double seconds) => checked((int)Math.Round(seconds * bpm / 60 * MusicMelody.Ticks / MusicMelody.Quantum)) * MusicMelody.Quantum;
                    return new MusicMelodyNote((int)n.NoteNumber, Tick(start), Math.Max(MusicMelody.Quantum, Tick(end) - Tick(start)), Math.Max(1, (int)n.Velocity));
                }).OrderBy(n => n.Start).ThenByDescending(n => n.Pitch).ToArray();
                var polyphonic = notes.Zip(notes.Skip(1), (a, b) => a.Start + a.Length > b.Start).Any(v => v);
                var warnings = new List<string> { "ImportDetails" };
                if (tempoEvents.Length > 1) warnings.Add("TempoFlattened");
                if (polyphonic) { warnings.Add("PolyphonyFlattened"); notes = UpperVoice(notes); }
                var melody = new MusicMelody { Bpm = bpm, Numerator = signature?.Numerator ?? 4, Denominator = signature?.Denominator ?? 4,
                    Notes = notes, SourceName = Path.GetFileName(path), ImportWarnings = warnings.ToArray() };
                melody.Validate();
                var name = track.Events.OfType<SequenceTrackNameEvent>().FirstOrDefault()?.Text;
                results.Add(new((string.IsNullOrWhiteSpace(name) ? "#" + trackIndex : name) + " · ch " + (channel.Key + 1), melody, polyphonic, original.Length));
            }
        }
        if (results.Count == 0) throw new InvalidDataException("Music.Midi.NoMelody");
        return results.ToArray();
    }

    private static MusicMelodyNote[] UpperVoice(MusicMelodyNote[] notes)
    {
        var boundaries = notes.SelectMany(n => new[] { n.Start, n.Start + n.Length }).Distinct().Order().ToArray();
        if (boundaries.Length > 8192) throw new InvalidDataException("Music.Midi.Limit");
        var result = new List<MusicMelodyNote>(); string? previousId = null;
        for (var i = 0; i < boundaries.Length - 1; i++) {
            var start = boundaries[i]; var end = boundaries[i + 1];
            var highest = notes.Where(n => n.Start <= start && n.Start + n.Length >= end).OrderByDescending(n => n.Pitch).FirstOrDefault();
            if (highest is null) { previousId = null; continue; }
            if (result.Count > 0 && previousId == highest.Id && result[^1].Start + result[^1].Length == start)
                result[^1] = result[^1] with { Length = end - result[^1].Start };
            else result.Add(new(highest.Pitch, start, end - start, highest.Velocity));
            previousId = highest.Id;
        }
        return result.ToArray();
    }

    public static void Write(string path, MusicMelody melody)
    {
        melody.Validate();
        var provenance = JsonSerializer.Serialize(new { app = "LOPATA", version = MusicSongMetadata.AppVersion, operation = "music.melody.export",
            created = DateTimeOffset.UtcNow, schema = 1, origin = "user melody; prior model history unknown", source = Path.GetFileName(melody.SourceName) });
        var track = new TrackChunk(new SequenceTrackNameEvent("LOPATA Melody"), new TextEvent(provenance),
            new SetTempoEvent(60_000_000L / melody.Bpm), new TimeSignatureEvent((byte)melody.Numerator, (byte)melody.Denominator));
        using (var manager = track.ManageNotes()) foreach (var n in melody.Notes)
            manager.Objects.Add(new Note((SevenBitNumber)n.Pitch, n.Length, n.Start) { Velocity = (SevenBitNumber)n.Velocity });
        var file = new MidiFile(track) { TimeDivision = new TicksPerQuarterNoteTimeDivision(MusicMelody.Ticks) };
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try { file.Write(temporary, format: MidiFileFormat.SingleTrack); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void SaveProject(string path, MusicMelody melody)
    {
        melody.Validate(); var data = new MusicMelodyDocument("LOPATA", 1, MusicSongMetadata.AppVersion, DateTimeOffset.UtcNow,
            melody.Snapshot() with { SourceName = Path.GetFileName(melody.SourceName) });
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true })); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static MusicMelody ReadProject(string path)
    {
        if (new FileInfo(path).Length > 2_000_000) throw new InvalidDataException("Music.Midi.Limit");
        var project = JsonSerializer.Deserialize<MusicMelodyDocument>(File.ReadAllText(path));
        if (project is not { App: "LOPATA", Schema: 1, Melody: not null }) throw new InvalidDataException("Music.Midi.Invalid");
        project.Melody.Validate(); return project.Melody.Snapshot();
    }
    private sealed record MusicMelodyDocument(string App, int Schema, string Version, DateTimeOffset Created, MusicMelody Melody);
}
