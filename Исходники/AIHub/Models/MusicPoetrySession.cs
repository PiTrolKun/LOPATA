using AIHub.Services;

namespace AIHub.Models;

public sealed record MusicPoetryParameters(string Variation, string Text);
public sealed record MusicPoetryPartial(DateTimeOffset At, string Text);
public sealed record MusicPoetryAttempt(string TurnId, DateTimeOffset At, int Attempt, string Raw, string Outcome, string[] Changes);
public sealed record MusicPoetryMessage(string Role, string Text, string TurnId)
{
    public bool InContext { get; set; }
    public bool IsProgrammatic { get; set; }
}

public sealed class MusicPoetrySession
{
    public int Schema { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool Persistent { get; set; }
    public string Lyrics { get; set; } = "";
    public MusicPoetryParameters Parameters { get; set; } = new(MusicStudioRuntime.Variation, "");
    public string Draft { get; set; } = "";
    public string ModelPath { get; set; } = "";
    public string ModelName { get; set; } = "";
    public string PartialReply { get; set; } = "";
    public List<MusicPoetryPartial> IncompleteReplies { get; set; } = [];
    public List<MusicPoetryAttempt> ReplyAttempts { get; set; } = [];
    public List<string> LyricsSteps { get; set; } = [];
    public List<MusicPoetryParameters> ParameterSteps { get; set; } = [];
    public int LyricsCursor { get; set; } = -1;
    public int ParameterCursor { get; set; } = -1;
    public List<MusicPoetryMessage> Messages { get; set; } = [];

    public void RememberText()
    {
        if (!string.IsNullOrWhiteSpace(Lyrics)) Persistent = true;
    }
    public void CommitLyrics()
    {
        RememberText();
        if (LyricsCursor >= 0 && LyricsSteps[LyricsCursor] == Lyrics) return;
        if (LyricsSteps.Count == 0 && Lyrics.Length == 0) return;
        LyricsSteps.Add(Lyrics); LyricsCursor = LyricsSteps.Count - 1;
    }
    public void CommitParameters()
    {
        if (ParameterCursor >= 0 && ParameterSteps[ParameterCursor] == Parameters) return;
        ParameterSteps.Add(Parameters); ParameterCursor = ParameterSteps.Count - 1;
    }
    public void NavigateLyrics(int delta)
    {
        var target = LyricsCursor + delta;
        CommitLyrics(); // preserve an unfinished edit before leaving it
        if (target >= 0 && target < LyricsSteps.Count) { LyricsCursor = target; Lyrics = LyricsSteps[target]; }
    }
    public void NavigateParameters(int delta)
    {
        var target = ParameterCursor + delta;
        CommitParameters();
        if (target >= 0 && target < ParameterSteps.Count) { ParameterCursor = target; Parameters = ParameterSteps[target]; }
    }
    public void Apply(MusicPoetryReply reply)
    {
        if (reply.Song is { } song) { CommitLyrics(); Lyrics = song; CommitLyrics(); }
        if (reply.Parameters is not null || reply.Variation is not null)
        {
            CommitParameters();
            Parameters = new(reply.Variation ?? Parameters.Variation, reply.Parameters ?? Parameters.Text);
            CommitParameters();
        }
        PartialReply = "";
    }
}
