using System.IO;
using System.Text.Json;

namespace AIHub.Services.VideoEditor;

internal sealed record VideoNarratorSample(string Id, string Language, string Speaker, string File, string Text, bool Experimental = false, string? SynthesisLanguage = null);
internal sealed record VideoNarratorSampleUnavailable(string Id, string Language, string ReasonKey);

/// <summary>Small application assets. Playback does not access the model library or start a runtime.</summary>
internal static class VideoNarratorSamples
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "Assets", "VideoNarratorSamples");
    private static readonly Lazy<VideoNarratorSample[]> Samples = new(Load);
    private static readonly Lazy<VideoNarratorSampleUnavailable[]> Unavailable = new(LoadUnavailable);
    internal static VideoNarratorSample? Find(string id, string language) => Samples.Value.FirstOrDefault(s => s.Id == id && s.Language == language && File.Exists(Path.Combine(Root, s.File)));
    internal static string? UnavailableReason(string id, string language) => Unavailable.Value.FirstOrDefault(s => s.Id == id && s.Language == language)?.ReasonKey;
    internal static string AudioPath(VideoNarratorSample sample) => Path.Combine(Root, sample.File);
    private static VideoNarratorSample[] Load()
    {
        try
        {
            var samples = JsonSerializer.Deserialize<VideoNarratorSample[]>(File.ReadAllText(Path.Combine(Root, "samples.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            return samples.Where(s => VideoNarratorCatalog.Models.Any(m => m.Id == s.Id && m.Speakers.Contains(s.Speaker)
                    && (m.Languages.Contains(s.Language) && !s.Experimental || s.Experimental && !m.Languages.Contains(s.Language) && m.Languages.Contains(s.SynthesisLanguage ?? "")))
                && s.Language is "ru" or "en" && s.File == s.Id + "-" + s.Language + ".wav").ToArray();
        }
        catch (IOException) { return []; }
        catch (JsonException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }
    private static VideoNarratorSampleUnavailable[] LoadUnavailable()
    {
        try { return JsonSerializer.Deserialize<VideoNarratorSampleUnavailable[]>(File.ReadAllText(Path.Combine(Root, "unavailable.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? []; }
        catch (IOException) { return []; }
        catch (JsonException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }
}
