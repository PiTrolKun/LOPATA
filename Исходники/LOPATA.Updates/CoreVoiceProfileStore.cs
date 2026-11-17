using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lopata.Updates;

public sealed record CoreVoiceSelection(string? LegacyProvider, string? PreferredProvider)
{
    [JsonIgnore] public string CompatibleProvider => CoreVoiceProfileStore.LegacyProvider(LegacyProvider) ?? CoreVoiceProfileStore.Espeak;
    [JsonIgnore] public string? SeparatePreference => PreferredProvider ??
        (CoreVoiceProfileStore.LegacyProvider(LegacyProvider) is null ? LegacyProvider : null);
    [JsonIgnore] public string Preferred => SeparatePreference ?? CompatibleProvider;
    public CoreVoiceSelection ForCompatibleStorage() => new(CompatibleProvider, SeparatePreference);
}

/// <summary>The shared settings file speaks the format understood by older builds.</summary>
public sealed class CoreVoiceProfileStore(string profileDirectory)
{
    public const string Espeak = "espeak";
    public const string RhVoice = "rhvoice";
    public const string Mini = "kitten2-mini";

    private static readonly JsonSerializerOptions SharedSettingsOptions = new() { WriteIndented = true };

    public string SettingsPath { get; } = Path.Combine(profileDirectory, "settings.json");
    private string PreferencePath { get; } = SafeUpdatePath.Resolve(profileDirectory, "core-voice-preference.json");
    private string LockPath { get; } = SafeUpdatePath.Resolve(profileDirectory, "core-voice-settings.lock");

    public static string? LegacyProvider(string? value) => value?.ToLowerInvariant() switch
    {
        Espeak => Espeak,
        RhVoice => RhVoice,
        _ => null
    };

    public IDisposable Acquire()
    {
        Directory.CreateDirectory(profileDirectory);
        SafeUpdatePath.RejectLinks(LockPath);
        for (var attempt = 0; ; attempt++)
        {
            try { return new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) when (attempt < 10 && (error.HResult & 0xffff) is 32 or 33)
            { Thread.Sleep(25); }
        }
    }

    // The caller holds the profile lock across a related read and write.
    public CoreVoiceSelection Read() => Read(ReadObject(SettingsPath));

    public CoreVoiceSelection Read(JsonObject settings) =>
        new(Provider(settings["coreVoice"] as JsonObject), Provider(ReadPreference()));

    public void SavePreference(string? provider)
    {
        var preference = ReadPreference();
        if (Provider(preference) == provider) return;
        preference["provider"] = provider;
        DurableUpdateFiles.WriteJson(PreferencePath, preference);
    }

    public void SaveCompatibleProvider(string provider)
    {
        var settings = ReadObject(SettingsPath);
        if (Provider(settings["coreVoice"] as JsonObject) == provider) return;
        SaveSharedSettings(settings, provider);
    }

    public void SaveSharedSettings(JsonObject settings, string provider)
    {
        if (LegacyProvider(provider) is not { } legacy) throw new ArgumentException("Unsupported legacy core voice.", nameof(provider));
        if (settings["coreVoice"] is not JsonObject voice)
            settings["coreVoice"] = voice = new JsonObject();
        voice["provider"] = legacy;
        DurableUpdateFiles.WriteJson(SettingsPath, settings, SharedSettingsOptions);
    }

    private static string? Provider(JsonObject? settings) =>
        settings?["provider"] is JsonValue property && property.TryGetValue<string>(out var value)
            ? value : null;

    private JsonObject ReadPreference()
    {
        try { return ReadObject(PreferencePath); }
        catch (Exception error) when (error is JsonException or InvalidDataException) { return new JsonObject(); }
    }

    private static JsonObject ReadObject(string path)
    {
        SafeUpdatePath.RejectLinks(path);
        return File.Exists(path)
            ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidDataException("Invalid voice settings.")
            : new JsonObject();
    }
}
