using System.Globalization;
using System.IO;
using AIHub.Models;

namespace AIHub.Services;

public sealed partial class LetItOutConversationStore
{
    internal int ContextCut(string id)
    {
        Id(id); using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT through_pair FROM context_cursor WHERE conversation_id=$id";
        command.Parameters.AddWithValue("$id", id);
        return Convert.ToInt32(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
    }

    internal bool SetContextCut(string id, int expected, int through)
    {
        Id(id); using var connection = Open(); using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$through", through);
        command.CommandText = "SELECT COALESCE((SELECT through_pair FROM context_cursor WHERE conversation_id=$id),0)";
        if (Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) != expected) return false;
        command.CommandText = "SELECT COUNT(*) FROM memory_pair WHERE conversation_id=$id AND number=$through AND number<(SELECT MAX(number) FROM memory_pair WHERE conversation_id=$id)";
        if (through <= expected || Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 1) return false;
        command.CommandText = "INSERT INTO context_cursor VALUES($id,$through) ON CONFLICT(conversation_id) DO UPDATE SET through_pair=$through";
        command.ExecuteNonQuery(); transaction.Commit(); return true;
    }

    internal static int ContextPairEnd(IReadOnlyList<LetItOutSavedMessage> saved, LetItOutMemoryPair pair)
        => saved.Take(pair.AnswerOrdinal + 1).Count(m => m.Role == "user" || m.Complete);

    internal int ContextStart(string id, IReadOnlyList<LetItOutChatMessage> history)
    {
        var cut = ContextCut(id); if (cut == 0) return 0;
        var source = Load(id) ?? throw new InvalidDataException("Conversation was removed.");
        var pair = MemoryPairs(id).Single(p => p.Number == cut);
        var end = ContextPairEnd(source.Messages, pair);
        if (end > history.Count || history[end - 1].Role != "assistant" || history[end - 1].Content != pair.Answer)
            throw new InvalidDataException("Context cursor does not match the original history.");
        return end;
    }
}
