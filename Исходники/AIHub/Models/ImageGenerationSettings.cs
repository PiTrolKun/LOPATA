namespace AIHub.Models;

public sealed class ImageGenerationSettings
{
    public string Folder { get; set; } = "";
    public int OutputLongestSide { get; set; }
    public AIHub.Services.ImageLora.ImageLoraSelection Loras { get; set; } = new();
}
