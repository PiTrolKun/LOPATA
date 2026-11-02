using System.IO;

namespace AIHub.Services.SongClip;

/// <summary>Host-owned MiniMax Ref2VA sections; user meaning stays distinct from scene development.</summary>
internal static class SongClipReferencePrompt
{
    internal static string Compose(SongClipAttempt attempt, SongClipVisualPlan plan, int index)
    {
        var scene = plan.Scenes.SingleOrDefault(value => value.Index == index)
            ?? throw new InvalidDataException(SongClipVisualProtocol.InvalidReply);
        if (attempt.Model != "h3_music_context" || attempt.References is not { Length: > 0 })
            return plan.GlobalStyle + "\n" + scene.Prompt;
        SongClipReferenceBrief.Validate(plan.ReferenceRoles, attempt);
        var roles = plan.ReferenceRoles!;
        var definitions = roles.Select(role => $"<Subject {role.Index}> is {Source(role.Kind)} in <Picture {role.Index}>. Its role in the clip: {role.Description}");
        var retention = roles.Select(role => $"<Subject {role.Index}>: fully_preserved - {Preservation(role.Kind)}");
        var use = roles.Select(role => $"<Subject {role.Index}> retains the role defined above.");
        return "subject_definitions:\n" + string.Join("\n", definitions)
            + "\n\nsummary:\n[reference generation] A music-video segment using the defined referenced content and the song-inspired events below."
            + "\n\nretention_analysis:\n" + string.Join("\n", retention)
            + "\n\ndetailed_description:\n" + plan.GlobalStyle + "\n[Shot 1] " + string.Join(" ", use) + " " + scene.Prompt
            + "\n\noverall_soundscape:\nThe supplied original song remains the final soundtrack. Do not add spoken narration or unrelated dialogue."
            + "\n\nnon_diegetic_music:\nThe original song is retained by the host without replacing its vocals or music.";
    }

    private static string Source(string kind) => kind switch {
        "performer" => "the performer or performers shown",
        "world" => "the setting or event world shown",
        "object" => "the object shown",
        _ => throw new InvalidDataException(SongClipVisualProtocol.InvalidReply)
    };

    private static string Preservation(string kind) => kind switch {
        "performer" => "Preserve the referenced performer identities, number, defining appearance and clothing when they appear. Follow their stated performance role; their actions and the surrounding song events may develop. Do not treat the reference sheet layout as the scene composition.",
        "world" => "Preserve the referenced setting and its defining spatial features as the world of the events. Characters, actions and camera placement may develop inside it; do not turn the setting into a performer.",
        "object" => "Preserve the referenced object's identity and defining shape. Use it according to its stated role in the events; placement, handling and camera framing may change.",
        _ => throw new InvalidDataException(SongClipVisualProtocol.InvalidReply)
    };
}
