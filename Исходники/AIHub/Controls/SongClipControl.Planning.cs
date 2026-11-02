using System.IO;
using System.Text.Json;
using AIHub.Services.SongClip;

namespace AIHub.Controls;

public sealed partial class SongClipControl
{
    private static SongClipProject FreshProject() => new()
    {
        Settings = new SongClipSettings { Prompt = "" }
    };

    // Importing a different song creates a fresh project. Reimporting the same
    // song must keep its direction and any immutable attempt awaiting continuation.
    internal void AdoptImportedSong(SongClipProject imported)
    {
        if (imported.Id != _project.Id) imported.Settings = imported.Settings with { Prompt = "" };
        _store.Save(imported);
        _project = imported;
    }

    // Planning is enabled only for new work. Returning the saved object intact
    // preserves legacy request identity and its committed model checkpoints.
    internal SongClipAttempt SelectStartAttempt(SongClipAttempt? restored = null)
    {
        if (restored is not null) return restored;
        if (_project.PendingAttemptId.Length > 0)
        {
            var previous = JsonSerializer.Deserialize<SongClipAttempt>(File.ReadAllText(
                _store.Resolve(_project.Id, "attempts/" + _project.PendingAttemptId + "/snapshot.json")));
            if (previous is not null && previous.Model == _project.Model
                && previous.Song == _project.Song && previous.Reference == _project.Reference
                && previous.Name == _project.Name && previous.Lyrics == _project.Lyrics
                && previous.Timings == _project.Timings
                && SongClipReferences.Same(previous.References, _project.References)
                && previous.Settings with { Seed = _project.Settings.Seed } == _project.Settings)
                return previous;
        }
        // Canonicalize only new work. Old matching null-reference snapshots above
        // must remain byte-for-byte legacy inputs on continuation.
        var references = SongClipReferences.For(_project).ToArray();
        _project.References = references; _project.Reference = references.FirstOrDefault()?.File ?? "";
        if (SongClipModes.IsMontage(_project.Settings.Motion.Mode) && _project.Settings.Montage is null)
            _project.Settings = _project.Settings with { Montage = new SongClipMontageSettings() };
        return new SongClipAttempt(Guid.NewGuid().ToString("N"), _project.Id,
            _project.Song, _project.Reference, _project.Name, _project.Lyrics, _project.Timings,
            _project.Settings with { Seed = _project.Settings.Seed < 0
                ? Random.Shared.NextInt64(0, int.MaxValue) : _project.Settings.Seed }, _root, _connections)
        {
            Model = _project.Model, PlanningVersion = 1,
            References = references.ToArray()
        };
    }
}
