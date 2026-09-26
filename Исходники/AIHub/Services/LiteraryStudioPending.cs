namespace AIHub.Services;

/// <summary>A recoverable composer snapshot, separate from conversation sent to the model.</summary>
public sealed record LiteraryStudioPending(string MessageId, string Text, List<StudioQuote> Quotes, bool WriterComment)
{
    public static void Restore(LiteraryStudioState state)
    {
        if (state.Pending is not { } pending) return;
        // Preserve the visible journal but do not send an unsuccessful request twice on retry.
        var sent = state.Messages.FirstOrDefault(m => m.Id == pending.MessageId);
        if (sent is not null) sent.InContext = false;
        if (state.Input.Length == 0) state.Input = pending.Text;
        foreach (var quote in pending.Quotes)
            if (!state.Quotes.Contains(quote)) state.Quotes.Add(quote);
        state.WriterComment = pending.WriterComment;
        state.Pending = null;
    }
}
