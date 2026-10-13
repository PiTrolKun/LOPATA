using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Ephemeral authentic history: never rewrites saved chat or includes rejected attempts.</summary>
internal static class MusicPoetryReasoningHistory
{
    public static IReadOnlyList<MusicPoetryMessage> Select(MusicPoetrySession session,
        MusicPoetryReasoningOptions options, string? modelId)
    {
        var original = MusicPoetryModelMessages.Select(session);
        if (!options.Enabled || !options.PreserveThoughts || !MusicPoetryReasoning.Extended(modelId)) return original;
        var result = new List<MusicPoetryMessage>();
        foreach (var turn in original.GroupBy(m => m.TurnId))
        {
            var messages = turn.ToArray();
            var accepted = session.ReplyAttempts.LastOrDefault(a => a.TurnId == turn.Key && a.Outcome == "applied"
                && !a.Changes.Contains("variation"));
            var thought = accepted is null ? null : session.Thoughts.LastOrDefault(t => t.TurnId == turn.Key
                && t.Attempt == accepted.Attempt && t.ModelId == modelId && t.State == MusicPoetryThought.Completed
                && !string.IsNullOrWhiteSpace(t.Text));
            if (thought is null) { result.AddRange(messages); continue; }
            var assistant = messages.LastOrDefault(m => m.Role == "assistant" && !m.IsProgrammatic);
            // A filtered assistant must not be restored through this path. Empty-chat applied replies have no such message.
            var emptyChat = false;
            if (assistant is null)
            {
                try { emptyChat = string.IsNullOrWhiteSpace(MusicPoetryProtocol.Parse(accepted!.Raw).Chat); }
                catch (Exception ex) when (ex is JsonException or System.IO.InvalidDataException) { }
            }
            if (assistant is null && !emptyChat) { result.AddRange(messages); continue; }
            foreach (var message in messages)
                result.Add(ReferenceEquals(message, assistant)
                    ? message with { Text = accepted!.Raw, ReasoningText = thought.Text, ContextSource = message }
                    : message);
            if (assistant is null) result.Add(new("assistant", accepted!.Raw, turn.Key) { ReasoningText = thought.Text });
        }
        return result;
    }

    public static object[] Messages(string system, IReadOnlyList<MusicPoetryMessage> messages) =>
        new object[] { new { role = "system", content = system } }.Concat(messages.Select(m => m.ReasoningText is null
            ? (object)new { role = m.Role, content = m.Text }
            : new { role = m.Role, content = m.Text, reasoning_content = m.ReasoningText })).ToArray();
}
