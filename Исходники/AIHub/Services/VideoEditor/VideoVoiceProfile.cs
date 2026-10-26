using System.IO;

namespace AIHub.Services.VideoEditor;

/// <summary>Serializable settings for an upstream voice, independent of existing generated media.</summary>
public sealed record VideoVoiceOptions(string Speaker = "", string Style = "", string Device = "auto", int Steps = 8, string Preset = "stable");
public sealed record VideoSpeechProfile(string Provider, string Language, int Rate, VideoVoiceOptions Options);
internal sealed record VideoNarratorChoice(string Id, string Language, int Rate, VideoVoiceOptions Options);

internal static class VideoVoiceProfiles
{
    internal static void Apply(VideoProject project, VideoNarratorChoice choice, bool global, IReadOnlySet<string> segments)
    {
        VideoNarratorCatalog.Validate(choice.Id, choice.Language, choice.Rate, choice.Options);
        // The most recently applied narrator is also the choice for whole narration.
        // Per-fragment overrides remain independent until explicitly selected below.
        var commonChanged = project.DefaultVoice != choice.Id || project.DefaultLanguage != choice.Language || project.DefaultRate != choice.Rate || project.DefaultVoiceOptions != choice.Options;
        if (commonChanged) foreach (var n in project.Narration.Where(n => n.AudioAssetId is not null
            && project.Tracks.SelectMany(t => t.Clips).Any(c => c.AssetId == n.AudioAssetId && c.NarrationId is null))) n.Stale = true;
        project.DefaultVoice = choice.Id; project.DefaultLanguage = choice.Language;
        project.DefaultRate = choice.Rate; project.DefaultVoiceOptions = choice.Options;
        if (!global) foreach (var n in project.Narration.Where(n => segments.Contains(n.Id)))
        {
            var changed = n.Provider != choice.Id || n.Language != choice.Language || n.Rate != choice.Rate || n.VoiceOptions != choice.Options;
            n.Provider = choice.Id; n.Language = choice.Language; n.Rate = choice.Rate; n.VoiceOptions = choice.Options;
            if (changed && n.AudioAssetId is not null) n.Stale = true;
        }
    }

    // Unknown older profiles remain readable; execution validates their availability without substituting a model.
    internal static void ValidateStored(VideoVoiceOptions? options)
    {
        if (options is null || options.Speaker is null || options.Style is null || options.Device is null || options.Preset is null
            || options.Speaker.Length > 160 || options.Style.Length > 2000 || options.Device.Length > 32 || options.Preset.Length > 32 || options.Steps is < 1 or > 50)
            throw new InvalidDataException("VideoEditor.InvalidProject");
    }
}
