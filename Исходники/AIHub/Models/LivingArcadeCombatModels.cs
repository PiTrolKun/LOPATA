namespace AIHub.Models;

public sealed record LivingArcadeCompanion
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "drone";
    public double Health { get; set; } = 24;
    public double MaxHealth { get; set; } = 24;
}
