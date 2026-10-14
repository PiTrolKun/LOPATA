namespace AIHub.Models;

public sealed class MusicPoetrySearchExchange
{
    public string TurnId { get; set; } = "";
    public string Question { get; set; } = "";
    public string Answer { get; set; } = "";
    public string[] Links { get; set; } = [];
    public string State { get; set; } = "preparing";
    public string? Error { get; set; }
}

public sealed record MusicPoetrySearchRequest(bool Preparing, string Question = "", string Answer = "", string[]? Links = null);
