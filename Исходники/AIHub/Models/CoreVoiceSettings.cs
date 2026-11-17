using AIHub.Services.VideoEditor;

namespace AIHub.Models;

public sealed class CoreVoiceSettings
{
    public const string EspeakProvider = "espeak";

    public const string RhVoiceProvider = "rhvoice";

    public const string KittenMiniProvider = VideoNarratorCatalog.KittenMini;

    public bool Enabled { get; set; } = true;

    public string Provider { get; set; } = KittenMiniProvider;

    public int Volume { get; set; } = 100;

    public int Rate { get; set; } = 120;

    public string RussianVoice { get; set; } = "ru";

    public string EnglishVoice { get; set; } = "en+f3";

    internal static string NormalizeProvider(string? provider) => provider?.ToLowerInvariant() switch
    {
        EspeakProvider => EspeakProvider,
        RhVoiceProvider => RhVoiceProvider,
        _ => KittenMiniProvider
    };
}

public sealed record CoreVoiceProviderOption(string Id, string DisplayName)
{
    public override string ToString() => DisplayName;
}
