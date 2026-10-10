using AIHub.Models;

namespace AIHub.Services;

/// <summary>Checks declared effects against the workspace, never infers success from model prose.</summary>
public static class MusicPoetryReplyGuard
{
    public sealed record Result(MusicPoetryReply Reply, string[] Changes, string? Error);

    public static Result Check(MusicPoetrySession before, MusicPoetryReply reply, bool assemble = false)
    {
        var changes = new List<string>();
        if (reply.Song is not null && !Equivalent(before.Lyrics, reply.Song)) changes.Add("song");
        if (reply.Parameters is not null && !Equivalent(before.Parameters.Text, reply.Parameters)) changes.Add("parameters");
        if (reply.Variation is not null && before.Parameters.Variation != reply.Variation) changes.Add("variation");
        foreach (var action in reply.Actions ?? [])
            if (!changes.Contains(action)) return new(reply, changes.ToArray(), "Music.Poetry.Missing." + action);
        if (changes.Except(reply.Actions ?? []).Any()) return new(reply, changes.ToArray(), "Music.Poetry.UndeclaredAction");
        // Discussion does not need fake copies of the editors. Assembly may legitimately reproduce a complete workspace.
        if (!assemble && changes.Count == 0 && (reply.Song is not null || reply.Parameters is not null || reply.Variation is not null))
            return new(reply, [], "Music.Poetry.UnchangedResult");
        if (!assemble && changes.Count == 0 && string.IsNullOrWhiteSpace(reply.Chat))
            return new(reply, [], "Music.Poetry.NoResult");
        return new(reply with { Song = changes.Contains("song") ? reply.Song : null,
            Parameters = changes.Contains("parameters") ? reply.Parameters : null,
            Variation = changes.Contains("variation") ? reply.Variation : null }, changes.ToArray(), null);
    }

    // Ignore line ending and trailing whitespace churn; substantive wording/punctuation edits still count.
    private static bool Equivalent(string a, string b) => Normalize(a) == Normalize(b);
    private static string Normalize(string text) => string.Join("\n",
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim().Split('\n').Select(line => line.TrimEnd()));
}
