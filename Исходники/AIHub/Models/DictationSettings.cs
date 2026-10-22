namespace AIHub.Models;

public sealed class DictationSettings
{
    public bool Prewarm { get; set; } = true;
    public bool HoldToTalk { get; set; }
    public int DeviceNumber { get; set; } = -1;
    public string Hotkey { get; set; } = "";
    private int _threads;
    public int Threads { get => _threads; set => _threads = Math.Clamp(value, 0, 16); }
    public int EffectiveThreads => Threads > 0 ? Threads : Math.Clamp(Environment.ProcessorCount / 4, 1, 8);
}
