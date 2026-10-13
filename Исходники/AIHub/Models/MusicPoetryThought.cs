namespace AIHub.Models;

/// <summary>Real model output; optional history use requires an accepted matching attempt and model ID.</summary>
public sealed class MusicPoetryThought
{
    public const string Active = "active", Completed = "completed", Interrupted = "interrupted";
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TurnId { get; set; } = "";
    public int Attempt { get; set; }
    public string ModelId { get; set; } = "";
    public int AnchorMessage { get; set; }
    public string Text { get; set; } = "";
    public string State { get; set; } = Active;
    public bool Expanded { get; set; } = true;
}
