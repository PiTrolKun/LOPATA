namespace AIHub.Models;

public enum CaptureSource { Window, Monitor, Desktop, Area }
public enum CaptureMode { Screenshot, Gif, Video }

public sealed class ScreenshotOutputSettings
{
    public bool Clipboard { get; set; } = true;
    public bool File { get; set; } = true;
}

public sealed class ScreenCaptureSettings
{
    public string Folder { get; set; } = "";
    public string ImageFormat { get; set; } = "png";
    public int ImageQuality { get; set; } = 90;
    public bool WebPLossless { get; set; } = true;
    public bool EnlargeArea { get; set; }
    public bool RestoreHiddenWindow { get; set; } = true;
    public bool IncludeCursor { get; set; } = true;
    public int GifSeconds { get; set; } = 15;
    public int GifFps { get; set; } = 10;
    public bool GifLoop { get; set; } = true;
    public bool GifShowControls { get; set; } = true;
    public int GifScalePercent { get; set; } = 100;
    public string VideoQuality { get; set; } = "original";
    public bool VideoShowControls { get; set; } = true;
    public string VideoFormat { get; set; } = "mp4";
    public int VideoFps { get; set; } = 30;
    public int VideoCompression { get; set; } = 75;
    public string AudioMode { get; set; } = "off";
    public string Microphone { get; set; } = "default";
    public string PlaybackDevice { get; set; } = "default";
    public string Processing { get; set; } = "cpu";
    public Dictionary<CaptureSource, ScreenshotOutputSettings> Outputs { get; set; } =
        Enum.GetValues<CaptureSource>().ToDictionary(s => s, _ => new ScreenshotOutputSettings());
    public Dictionary<string, int[]> Hotkeys { get; set; } = DefaultHotkeys();

    public static string Command(CaptureMode mode, CaptureSource source) => $"{mode}.{source}";
    public static IEnumerable<CaptureSource> Sources(CaptureMode mode) => Enum.GetValues<CaptureSource>()
        .Where(source => mode != CaptureMode.Gif || source != CaptureSource.Desktop);
    public static Dictionary<string, int[]> DefaultHotkeys() => new()
    {
        [Command(CaptureMode.Screenshot, CaptureSource.Window)] = [0x11, 0x12, 0x31],
        [Command(CaptureMode.Screenshot, CaptureSource.Monitor)] = [0x11, 0x12, 0x32],
        [Command(CaptureMode.Screenshot, CaptureSource.Desktop)] = [0x11, 0x12, 0x33],
        [Command(CaptureMode.Screenshot, CaptureSource.Area)] = [0x11, 0x12, 0x34]
    };

    public void Normalize()
    {
        if (!string.IsNullOrWhiteSpace(Folder))
        {
            try { Folder = System.IO.Path.GetFullPath(Folder); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or System.IO.IOException) { Folder = ""; }
        }
        Outputs ??= []; Hotkeys ??= DefaultHotkeys();
        foreach (var source in Enum.GetValues<CaptureSource>())
            if (!Outputs.TryGetValue(source, out var output) || output is null) Outputs[source] = new();
        ImageFormat = ImageFormat is "png" or "jpeg" or "webp" ? ImageFormat : "png";
        ImageQuality = Math.Clamp(ImageQuality, 1, 100);
        GifSeconds = ((Math.Clamp(GifSeconds, 5, 60) + 2) / 5) * 5;
        GifFps = GifFps is 5 or 10 or 15 ? GifFps : 10;
        GifScalePercent = GifScalePercent is 100 or 75 or 50 or 25 ? GifScalePercent : 100;
        Hotkeys.Remove(Command(CaptureMode.Gif, CaptureSource.Desktop));
        VideoFps = VideoFps is 15 or 24 or 30 or 60 ? VideoFps : 30;
        VideoCompression = Math.Clamp(VideoCompression, 1, 100);
        VideoQuality = VideoQuality is "original" or "720p" or "1080p" or "1440p" or "4K" ? VideoQuality : "original";
        VideoFormat = VideoFormat is "mp4" or "mkv" or "webm" ? VideoFormat : "mp4";
        AudioMode = AudioMode is "off" or "pc" or "mic" or "both" ? AudioMode : "off";
        Processing = Processing is "auto" or "cpu" or "gpu" ? Processing : "cpu";
    }
}
