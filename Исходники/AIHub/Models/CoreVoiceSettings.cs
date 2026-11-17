using System.Text.Json.Serialization;
using Lopata.Updates;

namespace AIHub.Models;

public sealed class CoreVoiceSettings
{
    public const string EspeakProvider = CoreVoiceProfileStore.Espeak;

    public const string RhVoiceProvider = CoreVoiceProfileStore.RhVoice;

    public const string KittenMiniProvider = CoreVoiceProfileStore.Mini;

    public bool Enabled { get; set; } = true;

    public string Provider { get; set; } = KittenMiniProvider;

    public int Volume { get; set; } = 100;

    public int Rate { get; set; } = 120;

    public string RussianVoice { get; set; } = "ru";

    public string EnglishVoice { get; set; } = "en+f3";

    [JsonIgnore] internal string FallbackProvider { get; set; } = EspeakProvider;

    internal string SelectedProvider => KnownProvider(Provider) ?? FallbackProvider;

    internal static string? KnownProvider(string? provider) => provider?.ToLowerInvariant() switch
    {
        EspeakProvider => EspeakProvider,
        RhVoiceProvider => RhVoiceProvider,
        KittenMiniProvider => KittenMiniProvider,
        _ => null
    };
}

public sealed record CoreVoiceProviderOption(string Id, string DisplayName)
{
    public override string ToString() => DisplayName;
}
