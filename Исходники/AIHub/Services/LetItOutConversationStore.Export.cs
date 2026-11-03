using System.IO;
using System.Text.Json;

namespace AIHub.Services;

public sealed partial class LetItOutConversationStore
{
    internal ChatAudioTranscript? ArchivedAudioTranscript(string id, string fileId)
    {
        var file = Attachment(id, fileId); using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT state FROM attachment_audio WHERE conversation_id=$id AND id=$f";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$f", fileId);
        if (cmd.ExecuteScalar() is not string json) return null;
        var value = JsonSerializer.Deserialize<ChatAudioTranscript>(json) ?? throw new InvalidDataException("Invalid archived transcript.");
        if (value.SourceHash != file.Sha256) throw new InvalidDataException("Archived transcript source changed.");
        return value; // Preserve its recorded ASR revision; export never requires today's recognizer.
    }

    internal string? RecordedSummaryModel(string id, int through)
    {
        Id(id); using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT model FROM memory_snapshot WHERE conversation_id=$id AND through_pair=$n";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$n", through);
        return cmd.ExecuteScalar() as string;
    }
}
