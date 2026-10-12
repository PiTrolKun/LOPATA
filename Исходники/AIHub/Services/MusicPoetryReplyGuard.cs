using AIHub.Models;

namespace AIHub.Services;

/// <summary>Derives effects from returned fields and the workspace, never from model prose.</summary>
public static class MusicPoetryReplyGuard
{
    public sealed record Result(MusicPoetryReply Reply, string[] Changes, string? Error);

    public static Result Check(MusicPoetrySession before, MusicPoetryReply reply, bool assemble = false)
    {
        if (reply.Variation is not null || reply.Actions?.Contains("variation") == true)
            return new(reply, [], "Music.Poetry.TargetReadOnly");
        if (reply.Song is not null && string.IsNullOrWhiteSpace(reply.Song))
            return new(reply, [], "Music.Poetry.Missing.song");
        if (reply.Parameters is not null)
        {
            var filtered = MusicPoetryBrief.Filter(MusicPoetryBrief.RemoveRepeatedSong(reply.Parameters, before.Lyrics, reply.Song));
            if (string.IsNullOrWhiteSpace(filtered))
                return new(reply, [], "Music.Poetry.Missing.parameters");
            reply = reply with { Parameters = filtered };
        }
        var changes = new List<string>();
        if (reply.Song is not null && !Equivalent(before.Lyrics, reply.Song)) changes.Add("song");
        if (reply.Parameters is not null && !Equivalent(MusicPoetryBrief.Parse(
            MusicPoetryBrief.RemoveRepeatedSong(before.Parameters.Text, before.Lyrics)).ToText(),
            MusicPoetryBrief.Parse(reply.Parameters).ToText())) changes.Add("parameters");
        foreach (var action in reply.Actions ?? [])
            if (!changes.Contains(action)) return new(reply, changes.ToArray(), "Music.Poetry.Missing." + action);
        // Legacy declarations may detect a missing promised result; omissions cannot discard real results.
        // Discussion does not need fake copies of the editors. Assembly may legitimately reproduce a complete workspace.
        if (!assemble && changes.Count == 0 && (reply.Song is not null || reply.Parameters is not null || reply.Variation is not null))
            return new(reply, [], "Music.Poetry.UnchangedResult");
        if (!assemble && changes.Count == 0 && string.IsNullOrWhiteSpace(reply.Chat))
            return new(reply, [], "Music.Poetry.NoResult");
        return new(reply with { Song = changes.Contains("song") ? reply.Song : null,
            Parameters = changes.Contains("parameters") ? reply.Parameters : null,
            Variation = null, Actions = changes.ToArray() }, changes.ToArray(), null);
    }

    // Ignore line ending and trailing whitespace churn; substantive wording/punctuation edits still count.
    private static bool Equivalent(string a, string b) => Normalize(a) == Normalize(b);
    private static string Normalize(string text) => string.Join("\n",
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim().Split('\n').Select(line => line.TrimEnd()));
}
