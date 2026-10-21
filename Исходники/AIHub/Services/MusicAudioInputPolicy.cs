using System.IO;

namespace AIHub.Services;

/// <summary>Project choices stay intact; only supported roles become immutable job inputs.</summary>
public static class MusicAudioInputPolicy
{
    public static bool AllowsReferences(string variation, MusicAudioTaskMode mode) =>
        mode != MusicAudioTaskMode.Restore && (variation == MusicAceCatalog.Variation
            || variation == MusicStudioRuntime.Variation && mode == MusicAudioTaskMode.NewSong);

    public static IEnumerable<MusicAudioInput> ActiveReferences(MusicAudioTask task, string variation) =>
        AllowsReferences(variation, task.Mode) ? task.References.Where(r => r.Enabled) : [];

    // Loading old history remains valid. Reject only execution of an already staged unsafe recipe.
    public static void ValidateExecution(MusicAudioTask task, string variation)
    {
        if (task.Enabled && !AllowsReferences(variation, task.Mode) && task.References.Any(r => r.Enabled))
            throw new InvalidDataException("Music.Transform.UnsupportedStagedReferences");
    }

    public static string ReferenceHintKey(string variation, MusicAudioTaskMode mode) =>
        "Music.Transform." + (mode == MusicAudioTaskMode.Restore ? "ReferencesBlockedProcessing"
            : variation == MusicStudioRuntime.Variation ? mode == MusicAudioTaskMode.NewSong ? "StudioReferenceRole" : "ReferencesBlockedStudioCover"
            : variation == MusicAceCatalog.Variation ? mode == MusicAudioTaskMode.NewSong ? "AceReferenceRole" : "AceCombinedRole"
            : "GenericHint");
}
