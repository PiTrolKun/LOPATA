using AIHub.Models;

namespace AIHub.Services;

/// <summary>Keep the saved conversation, but do not replay obsolete control instructions to the model.</summary>
internal static class MusicPoetryModelMessages
{
    public static IReadOnlyList<MusicPoetryMessage> Select(MusicPoetrySession session)
    {
        var latest = session.Messages.LastOrDefault(m => m.Role == "user" && !m.IsProgrammatic)?.TurnId;
        var oldEngineTurns = session.ReplyAttempts.Where(a => a.Outcome == "applied" && a.Changes.Contains("variation"))
            .Select(a => a.TurnId).ToHashSet();
        var otherEngines = MusicPoetryProtocol.Variations.Where(id => id != session.Parameters.Variation)
            .SelectMany(id => new[] { id, MusicModelVariants.Name(id) }).ToArray();
        return session.Messages.Where(m => m.Role is "user" or "assistant")
            .Where(m => !m.IsProgrammatic || (m.Role == "user" && m.TurnId == latest
                && m.ContextRevision == MusicPoetryProtocol.ContextRevision))
            .Where(m => m.Role != "assistant" || (!oldEngineTurns.Contains(m.TurnId)
                && !otherEngines.Any(name => m.Text.Contains(name, StringComparison.OrdinalIgnoreCase)))).ToArray();
    }

    public static void Mark(MusicPoetrySession session, IReadOnlyList<MusicPoetryMessage> included)
    {
        var references = new HashSet<MusicPoetryMessage>(included, ReferenceEqualityComparer.Instance);
        foreach (var message in session.Messages) message.InContext = references.Contains(message);
    }
}
