using System.IO;
using System.Text.Json;
using AIHub.Services.Dictation;
using Microsoft.Data.Sqlite;

namespace AIHub.Services;

internal sealed record ChatAudioPart(double Start, double End, string Text);
internal sealed record ChatAudioTranscript(string SourceHash, string Revision, double Duration,
    double Next, bool Complete, IReadOnlyList<ChatAudioPart> Parts);

public sealed partial class LetItOutConversationStore
{
    private static readonly HashSet<string> AttachmentAudio = new([".wav", ".mp3", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".webm"], StringComparer.OrdinalIgnoreCase);
    internal static bool IsAudioAttachment(string path) => AttachmentAudio.Contains(Path.GetExtension(path));
    private static void InitializeAudioAttachments(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS attachment_audio(conversation_id TEXT NOT NULL,id TEXT NOT NULL,state TEXT NOT NULL,PRIMARY KEY(conversation_id,id),FOREIGN KEY(conversation_id,id) REFERENCES attachment(conversation_id,id) ON DELETE CASCADE)";
        cmd.ExecuteNonQuery();
    }
    internal ChatAudioTranscript? AudioTranscript(string conversation, string id)
    {
        var file = Attachment(conversation, id);
        if (file.Kind != "audio") throw new InvalidDataException("LetItOut.Attachments.Invalid");
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT state FROM attachment_audio WHERE conversation_id=$c AND id=$id";
        cmd.Parameters.AddWithValue("$c", conversation); cmd.Parameters.AddWithValue("$id", id);
        var state = cmd.ExecuteScalar() as string;
        if (state is null) return null;
        var result = JsonSerializer.Deserialize<ChatAudioTranscript>(state) ?? throw new InvalidDataException("LetItOut.Attachments.Invalid");
        if (result.SourceHash != file.Sha256 || result.Revision != DictationComponents.Revision)
            throw new InvalidDataException("LetItOut.Attachments.AudioChanged");
        return result;
    }
    internal void SaveAudioTranscript(string conversation, string id, ChatAudioTranscript state)
    {
        var file = Attachment(conversation, id);
        if (file.Kind != "audio" || state.SourceHash != file.Sha256 || state.Revision != DictationComponents.Revision
            || !double.IsFinite(state.Next) || state.Next < 0 || state.Next > state.Duration)
            throw new InvalidDataException("LetItOut.Attachments.Invalid");
        using var db = Open(); using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO attachment_audio VALUES($c,$id,$s) ON CONFLICT(conversation_id,id) DO UPDATE SET state=$s";
        cmd.Parameters.AddWithValue("$c", conversation); cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$s", JsonSerializer.Serialize(state)); cmd.ExecuteNonQuery();
        if (state.Complete)
        {
            var text = LetItOutAudioTranscription.Join(state.Parts);
            var updated = file with { Preview = "Автораспознавание речи (возможны ошибки): " + (text.Length == 0 ? "Речь не распознана." : text.TruncateMemory(230)) };
            cmd.CommandText = "UPDATE attachment SET metadata=$s WHERE conversation_id=$c AND id=$id";
            cmd.Parameters["$s"].Value = JsonSerializer.Serialize(updated); cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
    private string ReadAudioTranscript(string conversation, AIHub.Models.LetItOutAttachment file, int offset, int maximum, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (offset < 0 || offset > SessionFileToolService.MaximumOffset || maximum < 1 || maximum > 16000) throw new ArgumentOutOfRangeException(nameof(offset));
        var state = AudioTranscript(conversation, file.Id);
        if (state is not { Complete: true }) throw new InvalidDataException("LetItOut.Attachments.AudioPending");
        var text = LetItOutAudioTranscription.Join(state.Parts);
        var noSpeech = text.Length == 0;
        if (noSpeech) text = "Распознаватель не смог получить текст речи. Это не доказательство отсутствия речи. Музыка, шумы, эмоции и иные звуки не анализировались.";
        var start = Math.Min(offset, text.Length); var end = Math.Min(text.Length, start + maximum);
        return JsonSerializer.Serialize(new { file_id = file.Id, name = file.Name,
            representation = "automatic_speech_transcript", model = "Parakeet TDT v3 INT8", revision = state.Revision,
            warning = "Возможны ошибки и чужая речь. Нет анализа любых звуков, эмоций или определения говорящих.",
            no_speech_recognized = noSpeech, offset, content = text[start..end], has_more = end < text.Length, next_offset = end });
    }
}
