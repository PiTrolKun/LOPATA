using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class LiteraryStudioControl
{
    private sealed record SubmittedRequest(string Text, List<StudioQuote> Quotes,
        IReadOnlyList<StudioMessage> Conversation, StudioMessage? Message);

    private SubmittedRequest? AcceptSubmission(bool transfer)
    {
        var input = State.Input; var quotes = State.Quotes.ToList();
        // The new user message is passed as the current instruction, not a duplicate history entry.
        var conversation = State.Messages.Where(m=>m.InContext && m.Complete && m.Session==State.Session).ToArray();
        var message = !transfer || input.Length > 0 || quotes.Count > 0
            ? State.Add("User",input.Length > 0 ? input : _l(transfer ? "Studio.TransferRequest" : "Studio.Action." + State.Action),quotes:quotes) : null;
        var comment = State.WriterComment;
        State.Pending = new(message?.Id ?? "", input, quotes, comment);
        State.Input = ""; State.Quotes.Clear(); State.WriterComment = false;
        if (Save()) return new(input,quotes,conversation,message);
        if (message is not null) State.Messages.Remove(message);
        State.Pending = null;
        State.Input = input; State.Quotes.AddRange(quotes); State.WriterComment = comment;
        return null;
    }
}
