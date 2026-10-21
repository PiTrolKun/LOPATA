using System.Text;
using AIHub.Models;

namespace AIHub.Services.MusicMidi;

/// <summary>The pinned YuE2 dialect: two voices, fixed header, bounded durations and explicit accidentals.</summary>
public static class MusicMelodyAbc
{
    private static readonly int[] Durations = [48, 32, 24, 16, 12, 8, 6, 4, 3, 2, 1];
    public static string Build(MusicMelody melody)
    {
        melody.Validate();
        if (melody.Notes.Length == 0) throw new System.IO.InvalidDataException("Music.Midi.Empty");
        var text = new StringBuilder($"X:1\nT:\nM:{melody.Numerator}/{melody.Denominator}\nL:1/32\nQ:1/4={melody.Bpm}\nV: Vocal clef=treble name=\"Vocal Melody\" snm=\"Vocal\"\nV: Ins clef=treble name=\"Ins Melody\" snm=\"Inst.\"\nK:C\n% verse\nV: Vocal\n");
        var at = 0;
        var barAt = 0; var barUnits = melody.Numerator * 32 / melody.Denominator;
        foreach (var note in melody.Notes.OrderBy(n => n.Start)) {
            Add("z", (note.Start - at) / MusicMelody.Quantum, false);
            Add(Pitch(note.Pitch), note.Length / MusicMelody.Quantum, true); at = note.Start + note.Length;
        }
        if (barAt != 0) text.Append('|'); text.Append("\nV: Ins\n"); barAt = 0;
        Add("z", melody.EndTick / MusicMelody.Quantum, false); if (barAt != 0) text.Append('|'); text.Append('\n');
        return text.ToString();
        void Add(string pitch, int units, bool tied) {
            while (units > 0) {
                var part = Durations.First(d => d <= units && d <= barUnits - barAt); units -= part; barAt += part;
                text.Append(pitch).Append(part); if (units > 0 && tied) text.Append('-'); text.Append(' ');
                if (barAt == barUnits) { text.Append("| "); barAt = 0; }
            }
        }
    }
    private static string Pitch(int midi)
    {
        var names = new[] { "=C", "^C", "=D", "^D", "=E", "=F", "^F", "=G", "^G", "=A", "^A", "=B" };
        var octave = midi / 12 - 1; var value = names[midi % 12];
        if (octave >= 5) return value.ToLowerInvariant() + new string('\'', octave - 5);
        return value + new string(',', Math.Max(0, 4 - octave));
    }
}
