namespace AIHub.Models;

public sealed class VoiceNotificationSettings
{
    public bool Enabled { get; set; } = true;
    public bool Errors { get; set; } = true;
    public bool Statuses { get; set; } = true;
    public bool Details { get; set; } = true;
    private double _volume = .75, _speed = 1;
    public double Volume { get => _volume; set => _volume = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : .75; }
    public double Speed { get => _speed; set => _speed = double.IsFinite(value) ? Math.Clamp(value, .75, 1.25) : 1; }
    public bool Allows(int group) => Enabled && (group switch { 1 => Errors, 2 => Statuses, 3 => Details, _ => false });
}
