using System.IO;
using AIHub.Models;

namespace AIHub.Services.MusicMidi;

/// <summary>Small deterministic CPU audition renderer; its timbre is not a generated performance.</summary>
public static class MusicMelodyWave
{
    public static void Write(string path, MusicMelody melody, CancellationToken token) {
        melody.Validate();
        if (melody.Notes.Length == 0) throw new InvalidDataException("Music.Midi.Empty");
        const int rate = 48000;
        var frames = checked((int)Math.Ceiling(melody.Seconds * rate));
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var w = new BinaryWriter(stream);
        w.Write(0x46464952u); w.Write(checked((uint)(frames * 4 + 36))); w.Write(0x45564157u);
        w.Write(0x20746d66u); w.Write(16u); w.Write((ushort)1); w.Write((ushort)2);
        w.Write(rate); w.Write(rate * 4); w.Write((ushort)4); w.Write((ushort)16); w.Write(0x61746164u); w.Write(frames * 4);
        var notes = melody.Notes.OrderBy(n => n.Start).ToArray(); var index = 0;
        for (var frame = 0; frame < frames; frame++) {
            if ((frame & 65535) == 0) token.ThrowIfCancellationRequested();
            var seconds = frame / (double)rate; var tick = seconds * melody.Bpm / 60 * MusicMelody.Ticks;
            while (index < notes.Length && tick >= notes[index].Start + notes[index].Length) index++;
            short sample = 0;
            if (index < notes.Length && tick >= notes[index].Start) {
                var note = notes[index]; var age = (tick - note.Start) / MusicMelody.Ticks * 60 / melody.Bpm;
                var remaining = (note.Start + note.Length - tick) / MusicMelody.Ticks * 60 / melody.Bpm;
                var envelope = Math.Min(1, age / .008) * Math.Min(1, remaining / .02);
                var phase = 2 * Math.PI * 440 * Math.Pow(2, (note.Pitch - 69) / 12d) * age;
                sample = (short)(6500 * note.Velocity / 127d * envelope * (Math.Sin(phase) + .2 * Math.Sin(phase * 2)));
            }
            w.Write(sample); w.Write(sample);
        }
        stream.Flush(true);
    }
}
