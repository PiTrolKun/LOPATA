using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIHub.Services.VoiceNotifications;

internal sealed record VoiceEventBinding(string Id, string ClipId, int Group);
internal sealed record VoiceClipText(string Id, Dictionary<string, string> Texts);
internal sealed record VoiceTextBinding(string EventId, string Language, string Text);
internal sealed record VoiceCatalogData(int Schema, VoiceClipText[] Clips, VoiceEventBinding[] Events, VoiceTextBinding[] Templates);

internal sealed class VoiceNotificationCatalog
{
    private readonly Dictionary<string, VoiceEventBinding> _events;
    private readonly Dictionary<string, VoiceClipText> _clips;
    private readonly Dictionary<string, VoiceEventBinding> _exact = new(StringComparer.Ordinal);
    private readonly List<(string Prefix, Regex Pattern, VoiceEventBinding Event)> _patterns = [];
    private readonly Dictionary<string, string> _speechTranslations = [];
    internal VoiceNotificationCatalog(VoiceCatalogData data, string language)
    {
        if (data.Schema != 1) throw new InvalidDataException("VoiceNotifications.InvalidPackage");
        _events = data.Events.DistinctBy(item => item.Id).ToDictionary(item => item.Id, StringComparer.Ordinal);
        _clips = data.Clips.ToDictionary(item => item.Id, StringComparer.Ordinal);
        foreach (var item in data.Templates.Where(item => item.Language.Length == 0 || item.Language == language))
        {
            if (!_events.TryGetValue(item.EventId, out var binding)) continue;
            var text = item.Text.Trim(); if (text.Length < 5) continue;
            if (!text.Contains('{')) { _exact.TryAdd(text, binding); continue; }
            var prefix = text[..text.IndexOf('{')]; if (prefix.Length < 7) continue;
            var pattern = Regex.Escape(text);
            pattern = Regex.Replace(pattern, @"\\\{[^}]+}", ".{1,1200}");
            _patterns.Add((prefix, new Regex("^" + pattern + "$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(15)), binding));
        }
    }
    internal static VoiceCatalogData Read(string path) => JsonSerializer.Deserialize<VoiceCatalogData>(File.ReadAllText(path))
        ?? throw new InvalidDataException("VoiceNotifications.InvalidPackage");
    internal VoiceEventBinding? Find(string key) => _events.GetValueOrDefault(key) ?? _events.GetValueOrDefault("localization:" + key)
        ?? _events.GetValueOrDefault("normalized:" + key);
    internal string? Text(string clip, string language) => _speechTranslations.GetValueOrDefault(clip) ?? _clips.GetValueOrDefault(clip)?.Texts.GetValueOrDefault(language);
    internal void AddTranslation(IReadOnlyDictionary<string, string> dictionary)
    {
        foreach (var (key, value) in dictionary)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (key.StartsWith("VoiceNotifications.Text.", StringComparison.Ordinal)) _speechTranslations[key[24..]] = value;
            else if (Find(key) is { } binding) _exact[value.Trim()] = binding;
        }
    }
    internal VoiceEventBinding? Match(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4000) return null;
        var text = value.Trim();
        if (Find(text) is { } direct) return direct;
        if (_exact.TryGetValue(text, out var exact)) return exact;
        foreach (var candidate in _patterns)
            if (text.StartsWith(candidate.Prefix, StringComparison.Ordinal))
                try { if (candidate.Pattern.IsMatch(text)) return candidate.Event; } catch (RegexMatchTimeoutException) { }
        return null;
    }
}
