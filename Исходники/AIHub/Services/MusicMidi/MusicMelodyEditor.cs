using AIHub.Models;

namespace AIHub.Services.MusicMidi;

/// <summary>Edits are atomic, including a mouse gesture. Invalid edits do not enter history.</summary>
public sealed class MusicMelodyEditor(MusicMelody initial)
{
    private readonly Stack<MusicMelody> _undo = new(), _redo = new();
    public MusicMelody Current { get; private set; } = initial.Snapshot();
    public bool CanUndo => _undo.Count != 0;
    public bool CanRedo => _redo.Count != 0;
    public void Set(MusicMelody next) {
        next.Validate();
        if (_undo.Count >= 100) { var recent = _undo.Take(99).Reverse().ToArray(); _undo.Clear(); foreach (var item in recent) _undo.Push(item); }
        _undo.Push(Current); _redo.Clear(); Current = next.Snapshot();
    }
    public void Undo() { if (!CanUndo) return; _redo.Push(Current); Current = _undo.Pop(); }
    public void Redo() { if (!CanRedo) return; _undo.Push(Current); Current = _redo.Pop(); }
    public void Remove(ISet<string> ids) => Set(Current with { Enabled = false, Notes = Current.Notes.Where(n => !ids.Contains(n.Id)).ToArray() });
    public void Transpose(ISet<string> ids, int semitones) => Set(Current with {
        Notes = Current.Notes.Select(n => ids.Contains(n.Id) ? n with { Pitch = n.Pitch + semitones } : n).ToArray() });
    public void Repeat(ISet<string> ids) {
        var notes = Current.Notes.Where(n => ids.Contains(n.Id)).ToArray(); if (notes.Length == 0) return;
        var length = notes.Max(n => n.Start + n.Length) - notes.Min(n => n.Start);
        Set(Current with { Notes = Current.Notes.Concat(notes.Select(n => n with { Id = Guid.NewGuid().ToString("N"), Start = n.Start + length })).ToArray() });
    }
}
