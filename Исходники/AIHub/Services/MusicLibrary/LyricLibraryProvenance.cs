using System.IO;
using AIHub.Models;

namespace AIHub.Services.MusicLibrary;

public static class LyricLibraryProvenance
{
    // A current generator selection is not evidence of lyric authorship. Only an exact stored
    // workshop result can establish this limited contribution; older unknown history stays unknown.
    public static OutputModelUse[] KnownModels(MusicProjectSnapshot snapshot) => snapshot.RhymeSteps
        .Where(s => s.ModelReady && !s.ModelSkipped && s.Reply.Length > 0 && s.Request.ModelPath is not null
            && s.Request.Source[..s.Request.Start] + s.Model + s.Request.Source[(s.Request.Start + s.Request.Length)..] == snapshot.Lyrics)
        .Select(s => new OutputModelUse(s.Request.ModelId, Path.GetFileName(s.Request.ModelPath!), "unknown",
            s.Request.CreativeMode ? "lyrics composition; exact stored workshop result" : "lyrics editing; exact stored workshop result"))
        .Distinct().ToArray();
}
