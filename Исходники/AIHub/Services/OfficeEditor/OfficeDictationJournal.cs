using System.Security.Cryptography;
using AIHub.Services.Dictation;

namespace AIHub.Services.OfficeEditor;

/// <summary>One audit entry per utterance. Intermediate hypotheses never create additional sheets or recordings.</summary>
internal sealed class OfficeDictationJournal(OfficeAiJournal journal, string requestLabel)
{
    private OfficeAiEntry? _utterance;

    internal async Task<string> RecognizeAsync(DictationChunk chunk, Func<Task<string>> recognize)
    {
        var input = FormattableString.Invariant($"{requestLabel} · PCM 16 kHz / 16 bit / mono · {chunk.Pcm.Length / 32000.0:0.###} s");
        var current = (_utterance ?? journal.NewRequest(input, null, DictationComponents.ModelId)) with
        {
            Request = input,
            Parameters = "SHA-256 " + Convert.ToHexString(SHA256.HashData(chunk.Pcm))
        };
        if (_utterance is null) await journal.WriteAsync(current);
        try
        {
            var transcript = await recognize();
            current = current with { Response = transcript, Models =
                [new(DictationComponents.ModelId, "Parakeet TDT v3 INT8", DictationComponents.Revision, "dictation")] };
            if (chunk.Final)
            {
                await journal.WriteAsync(current with { Status = "completed" });
                _utterance = null;
            }
            else _utterance = current;
            return transcript;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            _utterance = null;
            await journal.InterruptedAsync(current, error);
            throw;
        }
    }
}
