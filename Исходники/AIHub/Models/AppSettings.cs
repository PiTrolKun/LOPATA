namespace AIHub.Models;

public sealed class AppSettings
{
    public ApplicationUpdateSettings Updates { get; set; } = new();
    public string LanguageCode { get; set; } = "ru";

    public bool LanguageWasChosen { get; set; }

    public CoreVoiceSettings CoreVoice { get; set; } = new();

    public CoreAutonomySettings CoreAutonomy { get; set; } = new();

    public ModelDownloadSettings ModelDownloads { get; set; } = new();

    public FileViewerSettings FileViewer { get; set; } = new();

    public InterfaceSettings Interface { get; set; } = new();

    public ImageAnalysisSpeechSettings ImageAnalysisSpeech { get; set; } = new();

    public Dictionary<string, ImageAnalysisHeavySpeechSettings> ImageAnalysisHeavySpeechProfiles { get; set; } = [];
}

public static class WindowStartupModes
{
    public const string RememberLast = "remember_last";
    public const string Maximized = "maximized";
    public const string HalfScreen = "half_screen";

    public static bool IsSupported(string? value) =>
        value is RememberLast or Maximized or HalfScreen;
}

public sealed class InterfaceSettings
{
    public const int MinimumTextScalePercent = 90;
    public const int MaximumTextScalePercent = 150;
    public const int DefaultTextScalePercent = 100;

    private int _textScalePercent = DefaultTextScalePercent;
    private string _windowStartupMode = WindowStartupModes.RememberLast;

    public int TextScalePercent
    {
        get => _textScalePercent;
        set => _textScalePercent = Math.Clamp(
            value,
            MinimumTextScalePercent,
            MaximumTextScalePercent);
    }

    public string WindowStartupMode
    {
        get => _windowStartupMode;
        set => _windowStartupMode = WindowStartupModes.IsSupported(value)
            ? value
            : WindowStartupModes.RememberLast;
    }

    public RememberedWindowPlacement LastWindowPlacement { get; set; } = new();
}

public sealed class RememberedWindowPlacement
{
    public bool HasValue { get; set; }

    public string MonitorDeviceName { get; set; } = string.Empty;

    public double LeftRatio { get; set; }

    public double TopRatio { get; set; }

    public double WidthRatio { get; set; } = 0.5;

    public double HeightRatio { get; set; } = 0.5;

    public bool WasMaximized { get; set; }
}

public sealed record WindowStartupModeOption(string Id, string DisplayName);

public sealed class ModelDownloadSettings
{
    private int _maximumParallelConnections;

    public int MaximumParallelConnections
    {
        get => _maximumParallelConnections;
        set => _maximumParallelConnections = value is 1 or 2 or 4 or 8 ? value : 0;
    }
}

public sealed record ModelDownloadConnectionOption(int Value, string DisplayName);

public sealed class CoreAutonomySettings
{
    public const int MinimumSeconds = 30;
    public const int MaximumSeconds = 180;
    public const int DefaultSeconds = 90;

    private int _maximumIndependentSearchSeconds = DefaultSeconds;

    public int MaximumIndependentSearchSeconds
    {
        get => _maximumIndependentSearchSeconds;
        set => _maximumIndependentSearchSeconds = Math.Clamp(
            value,
            MinimumSeconds,
            MaximumSeconds);
    }
}

public sealed class FileViewerSettings
{
    public bool PreferInternalViewers { get; set; } = true;

    public Dictionary<string, bool> PreferInternalByExtension { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}
